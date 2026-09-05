using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class AdSmNumSampleDateTests
{
    [Theory]
    [InlineData(2026, 9, 2026, 9, 1)]
    [InlineData(2026, 0, 2026, 1, 1)]
    [InlineData(0, 0, 2000, 1, 1)]
    public void ForPeriod_matches_service_rules(short year, short month, int ey, int em, int ed)
    {
        Assert.Equal(new DateTime(ey, em, ed), AdSmNumSampleDate.ForPeriod(year, month));
    }

    [Theory]
    [InlineData(0, 0, DocumentNumberFormatter.DateMode.Continuous)]
    [InlineData(2026, 0, DocumentNumberFormatter.DateMode.Yearly)]
    [InlineData(2026, 9, DocumentNumberFormatter.DateMode.Monthly)]
    public void ModeForPeriod_matches_matrix(short year, short month, DocumentNumberFormatter.DateMode expected)
    {
        Assert.Equal(expected, AdSmNumSampleDate.ModeForPeriod(year, month));
    }
}

public class AdSmNumAdminServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AdSmNumAdminServiceTests()
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

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    // ── Continuous CRUD ─────────────────────────────────────────────────

    [Fact]
    public async Task Continuous_create_list_edit_delete()
    {
        var sut = CreateSut();
        var create = await sut.SaveContinuousAsync(new AdSmNumEditVm
        {
            NumCd = "inv",
            Prefix = "INV",
            TotLength = 10,
            Seq = 1,
            NumDes = "Invoice"
        }, isNew: true);
        Assert.True(create.Succeeded, create.Message);
        Assert.Equal("INV", create.Data!.NumCd);

        var list = await sut.ListContinuousAsync();
        Assert.True(list.Succeeded);
        Assert.Contains(list.Data!, r => r.NumCd == "INV");

        var get = await sut.GetContinuousAsync("INV");
        Assert.True(get.Succeeded);
        get.Data!.Seq = 5;
        get.Data.NumDes = "Updated";
        var update = await sut.SaveContinuousAsync(get.Data, isNew: false);
        Assert.True(update.Succeeded, update.Message);
        Assert.Equal(5, update.Data!.Seq);

        var delCheck = await sut.CanDeleteContinuousAsync(["INV"]);
        Assert.True(delCheck.CanDelete);
        var del = await sut.DeleteContinuousAsync(["INV"]);
        Assert.True(del.Succeeded, del.Message);
        Assert.False((await sut.ListContinuousAsync()).Data!.Any());
    }

    [Fact]
    public async Task Period_admin_create_starts_at_seq_1()
    {
        var sut = CreateSut();
        var create = await sut.SavePeriodAsync(new AdSmNumDateEditVm
        {
            NumCd = "INV",
            Year = 2026,
            Month = 9,
            Prefix = "INV",
            TotLength = 4,
            Seq = 1,
            NumberingDelimeter = "-"
        }, isNew: true);
        Assert.True(create.Succeeded, create.Message);
        Assert.Equal(1, create.Data!.Seq);
        Assert.Equal((short)2026, create.Data.Year);
        Assert.Equal((short)9, create.Data.Month);
    }

    [Fact]
    public async Task Period_duplicate_year_month_fails()
    {
        var sut = CreateSut();
        var first = await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true);
        Assert.True(first.Succeeded, first.Message);
        var second = await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true);
        Assert.False(second.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, second.ErrorCode);
    }

    [Fact]
    public async Task Mutual_exclusion_both_directions()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);
        var periodBlocked = await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true);
        Assert.False(periodBlocked.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, periodBlocked.ErrorCode);
        Assert.Contains("Continuous", periodBlocked.Message);

        await sut.DeleteContinuousAsync(["INV"]);
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        var contBlocked = await sut.SaveContinuousAsync(ContinuousVm(), isNew: true);
        Assert.False(contBlocked.Succeeded);
        Assert.Contains("Period", contBlocked.Message);
    }

    [Fact]
    public async Task Other_branch_not_listed_or_accessible()
    {
        var hq = CreateSut(branch: "HQ");
        Assert.True((await hq.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);

        var other = CreateSut(branch: "BR2");
        var list = await other.ListContinuousAsync();
        Assert.True(list.Succeeded);
        Assert.Empty(list.Data!);

        var get = await other.GetContinuousAsync("INV");
        Assert.False(get.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, get.ErrorCode);
    }

    [Fact]
    public async Task No_location_list_ok_save_invalid_scope()
    {
        var listSut = CreateSut(location: null);
        var list = await listSut.ListContinuousAsync();
        Assert.True(list.Succeeded);

        var saveSut = CreateSut(location: null);
        var save = await saveSut.SaveContinuousAsync(ContinuousVm(), isNew: true);
        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, save.ErrorCode);
    }

    [Fact]
    public async Task Permissions_enforced()
    {
        var noAdd = CreateSut(canAdd: false);
        Assert.Equal(IvMasterErrorCode.AccessDenied,
            (await noAdd.SaveContinuousAsync(ContinuousVm(), isNew: true)).ErrorCode);

        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);

        var noEdit = CreateSut(canEdit: false);
        var get = await CreateSut().GetContinuousAsync("INV");
        Assert.Equal(IvMasterErrorCode.AccessDenied,
            (await noEdit.SaveContinuousAsync(get.Data!, isNew: false)).ErrorCode);

        var noDelete = CreateSut(canDelete: false);
        Assert.Equal(IvMasterErrorCode.AccessDenied,
            (await noDelete.DeleteContinuousAsync(["INV"])).ErrorCode);
    }

    // ── Year / Month ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 9, false)]
    [InlineData(2026, 0, true)]
    [InlineData(2026, 9, true)]
    public async Task Year_month_matrix(short year, short month, bool ok)
    {
        var sut = CreateSut();
        var result = await sut.SavePeriodAsync(PeriodVm(year, month, numCd: $"N{year}{month}"), isNew: true);
        Assert.Equal(ok, result.Succeeded);
        if (ok)
        {
            Assert.Equal(year, result.Data!.Year);
            Assert.Equal(month, result.Data.Month);
        }
        else
        {
            Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        }
    }

    // ── Freeze / namespace ────────────────────────────────────────────────

    [Fact]
    public async Task Prefix_change_while_seq_1_and_unused_allowed()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);
        var get = await sut.GetContinuousAsync("INV");
        get.Data!.Prefix = "XXX";
        get.Data.TotLength = 10;
        var update = await sut.SaveContinuousAsync(get.Data, isNew: false);
        Assert.True(update.Succeeded, update.Message);
        Assert.Equal("XXX", update.Data!.Prefix);
    }

    [Fact]
    public async Task Prefix_change_while_seq_gt_1_rejected()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(seq: 2), isNew: true)).Succeeded);
        // Seed with Seq=2 via insert then bump: insert at 1 then raise
        await sut.DeleteContinuousAsync(["INV"]);
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);
        var get = await sut.GetContinuousAsync("INV");
        get.Data!.Seq = 2;
        Assert.True((await sut.SaveContinuousAsync(get.Data, isNew: false)).Succeeded);

        get = await sut.GetContinuousAsync("INV");
        get.Data!.Prefix = "ZZZ";
        var blocked = await sut.SaveContinuousAsync(get.Data, isNew: false);
        Assert.False(blocked.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, blocked.ErrorCode);
        Assert.Contains("cannot be changed", blocked.Message);
    }

    [Fact]
    public async Task Period_format_fields_frozen_after_use()
    {
        var sut = CreateSut();
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        var get = await sut.GetPeriodAsync((await sut.ListPeriodAsync()).Data![0].Uid);
        get.Data!.Seq = 2;
        Assert.True((await sut.SavePeriodAsync(get.Data, isNew: false)).Succeeded);

        get = await sut.GetPeriodAsync(get.Data.Uid);
        get.Data!.NumberingDelimeter = "*";
        var blocked = await sut.SavePeriodAsync(get.Data, isNew: false);
        Assert.False(blocked.Succeeded);
        Assert.Contains("cannot be changed", blocked.Message);
    }

    [Fact]
    public async Task Prefix_change_seq_1_but_next_invno_exists_rejected()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);
        await SeedInvoiceAsync("XXX0000001");

        var get = await sut.GetContinuousAsync("INV");
        get.Data!.Prefix = "XXX";
        var blocked = await sut.SaveContinuousAsync(get.Data, isNew: false);
        Assert.False(blocked.Succeeded);
        Assert.Contains("already exists", blocked.Message);
    }

    // ── Number reuse ────────────────────────────────────────────────────

    [Fact]
    public async Task Lower_continuous_seq_rejected()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);
        var get = await sut.GetContinuousAsync("INV");
        get.Data!.Seq = 5;
        Assert.True((await sut.SaveContinuousAsync(get.Data, isNew: false)).Succeeded);

        get = await sut.GetContinuousAsync("INV");
        get.Data!.Seq = 3;
        var blocked = await sut.SaveContinuousAsync(get.Data, isNew: false);
        Assert.False(blocked.Succeeded);
        Assert.Contains("cannot be reduced", blocked.Message);
    }

    [Fact]
    public async Task Lower_period_seq_rejected()
    {
        var sut = CreateSut();
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        var get = await sut.GetPeriodAsync((await sut.ListPeriodAsync()).Data![0].Uid);
        get.Data!.Seq = 4;
        Assert.True((await sut.SavePeriodAsync(get.Data, isNew: false)).Succeeded);

        get = await sut.GetPeriodAsync(get.Data.Uid);
        get.Data!.Seq = 2;
        var blocked = await sut.SavePeriodAsync(get.Data, isNew: false);
        Assert.False(blocked.Succeeded);
        Assert.Contains("cannot be reduced", blocked.Message);
    }

    [Fact]
    public async Task Delete_does_not_decrement_remaining_seq()
    {
        var sut = CreateSut();
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 10, numCd: "INV"), isNew: true)).Succeeded);
        var list = await sut.ListPeriodAsync();
        var sep = list.Data!.Single(r => r.Month == 9);
        var get = await sut.GetPeriodAsync(sep.Uid);
        get.Data!.Seq = 7;
        Assert.True((await sut.SavePeriodAsync(get.Data, isNew: false)).Succeeded);

        var oct = list.Data!.Single(r => r.Month == 10);
        await sut.DeletePeriodAsync([new AdSmNumDateKey { Uid = oct.Uid, RowVersion = oct.RowVersion }]);

        var after = await sut.GetPeriodAsync(sep.Uid);
        Assert.Equal(7, after.Data!.Seq);
    }

    [Fact]
    public async Task Recreate_period_seq1_when_invno_exists_rejected()
    {
        var sut = CreateSut();
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        await SeedInvoiceAsync("INV2609-0001");

        var row = (await sut.ListPeriodAsync()).Data![0];
        Assert.True((await sut.DeletePeriodAsync([new AdSmNumDateKey { Uid = row.Uid, RowVersion = row.RowVersion }])).Succeeded);

        var recreate = await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true);
        Assert.False(recreate.Succeeded);
        Assert.Contains("already exists", recreate.Message);

        var ok = await sut.SavePeriodAsync(PeriodVm(2026, 9, seq: 2), isNew: true);
        Assert.True(ok.Succeeded, ok.Message);
    }

    // ── Concurrency ─────────────────────────────────────────────────────

    [Fact]
    public async Task Continuous_seq_optimistic_guard_zero_rows_when_seq_mismatched()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveContinuousAsync(ContinuousVm(), isNew: true)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AdSmNum SET Seq = {5} WHERE CompanyCode = {"DEMO"} AND BranchCode = {"HQ"} AND NumCd = {"INV"}");

        // Same predicate the service uses: 0 rows when Seq already moved.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE AdSmNum
SET Seq = {6}
WHERE CompanyCode = {"DEMO"} AND BranchCode = {"HQ"} AND NumCd = {"INV"} AND Seq = {1}");
        Assert.Equal(0, affected);

        // Sequential saves still succeed via reload of current Seq.
        var get = await sut.GetContinuousAsync("INV");
        Assert.Equal(5, get.Data!.Seq);
        get.Data.Seq = 6;
        Assert.True((await sut.SaveContinuousAsync(get.Data, isNew: false)).Succeeded);
    }

    [Fact]
    public async Task Period_stale_rowversion_concurrency()
    {
        var sut = CreateSut();
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        var get1 = await sut.GetPeriodAsync((await sut.ListPeriodAsync()).Data![0].Uid);
        var get2 = await sut.GetPeriodAsync(get1.Data!.Uid);

        get1.Data!.NumDes = "First";
        Assert.True((await sut.SavePeriodAsync(get1.Data, isNew: false)).Succeeded);

        get2.Data!.NumDes = "Stale";
        var stale = await sut.SavePeriodAsync(get2.Data, isNew: false);
        Assert.False(stale.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, stale.ErrorCode);
    }

    [Fact]
    public async Task Period_stale_rowversion_delete_concurrency()
    {
        var sut = CreateSut();
        Assert.True((await sut.SavePeriodAsync(PeriodVm(2026, 9), isNew: true)).Succeeded);
        var list = await sut.ListPeriodAsync();
        var row = list.Data![0];
        var staleRv = (byte[])row.RowVersion.Clone();

        var get = await sut.GetPeriodAsync(row.Uid);
        get.Data!.NumDes = "bump";
        Assert.True((await sut.SavePeriodAsync(get.Data, isNew: false)).Succeeded);

        var del = await sut.DeletePeriodAsync([new AdSmNumDateKey { Uid = row.Uid, RowVersion = staleRv }]);
        Assert.False(del.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, del.ErrorCode);
    }

    // ── Formatter validation ────────────────────────────────────────────

    [Fact]
    public async Task Blank_prefix_and_missing_format_token_rejected()
    {
        var sut = CreateSut();
        var blank = await sut.SaveContinuousAsync(new AdSmNumEditVm
        {
            NumCd = "INV",
            Prefix = "  ",
            TotLength = 10,
            Seq = 1
        }, isNew: true);
        Assert.False(blank.Succeeded);

        var badFormat = await sut.SavePeriodAsync(new AdSmNumDateEditVm
        {
            NumCd = "FMT",
            Year = 2026,
            Month = 9,
            Prefix = "INV",
            TotLength = 4,
            Seq = 1,
            NumberingFormat = "NO_TOKEN"
        }, isNew: true);
        Assert.False(badFormat.Succeeded);
        Assert.Contains("{1}", badFormat.ValidationErrors.Values.FirstOrDefault() ?? badFormat.Message);
    }

    [Fact]
    public async Task Period_prefix_20_ok_numcd_11_rejected()
    {
        var sut = CreateSut();
        var longPrefix = await sut.SavePeriodAsync(new AdSmNumDateEditVm
        {
            NumCd = "INV",
            Year = 2026,
            Month = 9,
            Prefix = new string('P', 20),
            TotLength = 4,
            Seq = 1,
            NumberingDelimeter = "-"
        }, isNew: true);
        Assert.True(longPrefix.Succeeded, longPrefix.Message);

        var longCd = await sut.SavePeriodAsync(new AdSmNumDateEditVm
        {
            NumCd = "ABCDEFGHIJK",
            Year = 2026,
            Month = 10,
            Prefix = "X",
            TotLength = 4,
            Seq = 1
        }, isNew: true);
        Assert.False(longCd.Succeeded);
    }

    [Fact]
    public async Task Formatted_length_over_30_rejected()
    {
        var sut = CreateSut();
        var result = await sut.SavePeriodAsync(new AdSmNumDateEditVm
        {
            NumCd = "INV",
            Year = 2026,
            Month = 9,
            Prefix = new string('A', 20),
            TotLength = 10,
            Seq = 1,
            NumberingDelimeter = "-----"
        }, isNew: true);
        // FormatDateMode monthly: Prefix(20) + yy(2) + MM(2) + delim(5) + seq(10) = 39 > 30
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private AdSmNumAdminService CreateSut(
        string company = "DEMO",
        string branch = "HQ",
        string? location = "MAIN",
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);

        return new AdSmNumAdminService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, branch, location),
            access.Object);
    }

    private async Task SeedInvoiceAsync(string invNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SaInvoices.Add(new SaInvoice
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            InvNo = invNo,
            CustCode = "C1",
            InvDate = new DateTime(2026, 9, 1),
            Status = "Open",
            DoNo = invNo,
            CurrRate = 1m,
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    private static AdSmNumEditVm ContinuousVm(long seq = 1) => new()
    {
        NumCd = "INV",
        Prefix = "INV",
        TotLength = 10,
        Seq = seq,
        NumDes = "Invoice"
    };

    private static AdSmNumDateEditVm PeriodVm(short year, short month, string numCd = "INV", long seq = 1) => new()
    {
        NumCd = numCd,
        Year = year,
        Month = month,
        Prefix = "INV",
        TotLength = 4,
        Seq = seq,
        NumberingDelimeter = "-"
    };
}
