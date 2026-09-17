using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ErpWeb.Tests;

/// <summary>
/// Sales Quotation service tests. The plan's Phase F list drives these: the state machine and its
/// invalid transitions, the expiry rules (including "ACCEPTED never auto-expires" and "ACCEPTED past
/// ValidUntil cannot convert"), revision mechanics, the conversion invariants, atomic rollback,
/// snapshot fidelity and tenant isolation.
/// </summary>
public class SaQtServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly MutableCurrentDateService _clock = new(FixedToday);

    public SaQtServiceTests()
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
                ICode = "SVC1",
                IDesc = "Service item",
                IClassCode = "RAW",
                StdUom = "EA",
                SellingUom = "EA",
                StockControl = false,
                IsActive = true,
                SellingPrice = 50m,
                SellingGlCode = "GLSVC"
            },
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
                DefWarehouse = "MAIN"
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
                ShipName = "Alpha",
                ShipAddress1 = "SHIP ADDR 1",
                ShipCity = "SHIP CITY",
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
                ShipName = "Beta",
                IsActive = true,
                RowVersion = [2, 0, 0, 0, 0, 0, 0, 0]
            });
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ProjCode = "PRJ1",
            ProjName = "Round-trip project",
            Status = MsProjectStatus.Active,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        });

        // A second tenant, to prove company/branch isolation.
        db.SaCurrencies.Add(new SaCurrency
        {
            CompanyCode = "OTHER",
            CurrCode = "MYR",
            IsActive = true
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ─────────────────────────────────────────────────────────────────────────────
    // Create / defaults
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveNew_creates_NEW_revision_1_with_default_validity()
    {
        var sut = CreateQtSut();

        var save = await sut.SaveNewAsync(QtRequest(qty: 10m, price: 12m));

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("QT2609-0001", save.QtNo);
        Assert.Equal(SaQtStatuses.New, save.Document!.Status);
        Assert.Equal(SaQtConversionStatuses.None, save.Document.ConversionStatus);
        Assert.Equal(1, save.Document.CustRel);
        Assert.Equal(1, save.Document.LastCustRel);
        Assert.True(save.Document.IsCurrent);
        Assert.Null(save.Document.RevisionReason);
        // Default validity = QT date + 30 days.
        Assert.Equal(FixedToday.AddDays(30), save.Document.ValidUntil);
        // Quotation is not an SO: no fulfilment state exists at all.
        Assert.Null(save.Document.ConvertedSoNo);
        Assert.False(save.Document.IsExpired);

        var line = Assert.Single(save.Document.Lines);
        Assert.Equal(10m, line.OrderQty);
        Assert.Equal(0m, line.ConvertedQty);
        Assert.Equal(10m, line.RemainingQty);
        Assert.Equal("QT", save.Document.Prefix);
    }

    [Fact]
    public async Task SaveNew_accepts_an_omitted_customer_reference()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.CustPo = null;

        var save = await sut.SaveNewAsync(request);

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Null(save.Document!.CustPo);
    }

    [Fact]
    public async Task SaveNew_rejects_valid_until_before_qt_date()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.ValidUntil = FixedToday.AddDays(-1);

        var save = await sut.SaveNewAsync(request);

        Assert.False(save.Succeeded);
        Assert.Equal(SaQtErrorKind.Validation, save.ErrorKind);
        Assert.True(save.ValidationErrors.ContainsKey("ValidUntil"));
    }

    [Fact]
    public async Task SaveNew_rejects_negative_unit_price()
    {
        var sut = CreateQtSut();

        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: -1m));

        Assert.False(save.Succeeded);
        Assert.True(save.ValidationErrors.ContainsKey("Lines[0].UnitPrice"));
    }

    [Fact]
    public async Task SaveNew_rejects_zero_quantity()
    {
        var sut = CreateQtSut();

        var save = await sut.SaveNewAsync(QtRequest(qty: 0m, price: 10m));

        Assert.False(save.Succeeded);
        Assert.True(save.ValidationErrors.ContainsKey("Lines[0].OrderQty"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Lifecycle: Send / Accept / Lose / Cancel
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_moves_new_to_sent_and_accept_moves_sent_to_accepted()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var send = await sut.SendAsync(Key(save.Document!));
        Assert.True(send.Succeeded, send.ErrorMessage);
        Assert.Equal(SaQtStatuses.Sent, send.Document!.Status);
        Assert.NotNull(send.Document.SentDate);
        Assert.Equal("admin", send.Document.SentBy);

        var accept = await sut.AcceptAsync(Key(send.Document));
        Assert.True(accept.Succeeded, accept.ErrorMessage);
        Assert.Equal(SaQtStatuses.Accepted, accept.Document!.Status);
        Assert.NotNull(accept.Document.AcceptedDate);

        // No other lifecycle field moved.
        Assert.Equal(SaQtConversionStatuses.None, accept.Document.ConversionStatus);
        Assert.Null(accept.Document.ClosedReason);
    }

    [Fact]
    public async Task Accept_on_a_new_quotation_is_rejected()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var accept = await sut.AcceptAsync(Key(save.Document!));

        Assert.False(accept.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, accept.ErrorKind);
    }

    [Fact]
    public async Task Send_on_an_already_sent_quotation_is_rejected()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var send = await sut.SendAsync(Key(save.Document!));

        var again = await sut.SendAsync(Key(send.Document!));

        Assert.False(again.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, again.ErrorKind);
    }

    [Fact]
    public async Task Lose_requires_a_reason()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var noReason = await sut.LoseAsync(Key(save.Document!), "   ");
        Assert.False(noReason.Succeeded);
        Assert.Equal(SaQtErrorKind.Validation, noReason.ErrorKind);
        Assert.True(noReason.ValidationErrors.ContainsKey("LostReason"));

        var lost = await sut.LoseAsync(Key(save.Document!), "Lost to competitor");
        Assert.True(lost.Succeeded, lost.ErrorMessage);
        Assert.Equal(SaQtStatuses.Lost, lost.Document!.Status);
        Assert.Equal("Lost to competitor", lost.Document.LostReason);
    }

    [Fact]
    public async Task Cancel_moves_sent_to_cancelled_and_is_terminal()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var send = await sut.SendAsync(Key(save.Document!));

        var cancel = await sut.CancelAsync(Key(send.Document!));
        Assert.True(cancel.Succeeded, cancel.ErrorMessage);
        Assert.Equal(SaQtStatuses.Cancelled, cancel.Document!.Status);

        // Terminal: it can no longer be sent.
        var sendAgain = await sut.SendAsync(Key(cancel.Document!));
        Assert.False(sendAgain.Succeeded);
    }

    [Fact]
    public async Task Accepted_quotation_cannot_be_cancelled_or_lost()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);

        var cancel = await sut.CancelAsync(Key(accepted.Document!));
        Assert.False(cancel.Succeeded);

        var lose = await sut.LoseAsync(Key(accepted.Document!), "nope");
        Assert.False(lose.Succeeded);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Expiry
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Today_equal_to_valid_until_is_not_expired()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.ValidUntil = FixedToday;
        var save = await sut.SaveNewAsync(request);

        var send = await sut.SendAsync(Key(save.Document!));

        Assert.True(send.Succeeded, send.ErrorMessage);
        Assert.Equal(SaQtStatuses.Sent, send.Document!.Status);
        Assert.False(send.Document.IsExpired);
    }

    [Fact]
    public async Task Send_on_a_lapsed_quotation_expires_it_instead()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.QtDate = FixedToday.AddDays(-10);
        request.ValidUntil = FixedToday.AddDays(-1);
        var save = await sut.SaveNewAsync(request);
        Assert.True(save.Succeeded, save.ErrorMessage);

        var send = await sut.SendAsync(Key(save.Document!));

        Assert.False(send.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, send.ErrorKind);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.SaQts.SingleAsync();
        Assert.Equal(SaQtStatuses.Expired, stored.Status);
        Assert.NotNull(stored.ExpiredDate);
        Assert.Null(stored.SentDate);
    }

    [Fact]
    public async Task Accept_after_expiry_is_rejected_and_the_quotation_becomes_expired()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.ValidUntil = FixedToday;
        var save = await sut.SaveNewAsync(request);
        var send = await sut.SendAsync(Key(save.Document!));
        Assert.True(send.Succeeded, send.ErrorMessage);

        // The clock moves past the offer window between Send and Accept.
        _clock.Today = FixedToday.AddDays(1);

        var accept = await sut.AcceptAsync(Key(send.Document!));

        Assert.False(accept.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, accept.ErrorKind);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.SaQts.SingleAsync();
        Assert.Equal(SaQtStatuses.Expired, stored.Status);
        Assert.Null(stored.AcceptedDate);
        Assert.NotNull(stored.ExpiredDate);
    }

    [Fact]
    public async Task Open_lazily_expires_a_lapsed_sent_quotation()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.ValidUntil = FixedToday;
        var save = await sut.SaveNewAsync(request);
        await sut.SendAsync(Key(save.Document!));

        _clock.Today = FixedToday.AddDays(3);

        var opened = await sut.GetAsync(save.QtNo!);

        Assert.True(opened.Succeeded, opened.ErrorMessage);
        Assert.Equal(SaQtStatuses.Expired, opened.Document!.Status);
        Assert.True(opened.Document.IsExpired);
    }

    [Fact]
    public async Task Accepted_quotation_never_auto_expires_but_conversion_is_blocked_past_valid_until()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.ValidUntil = FixedToday;
        var save = await sut.SaveNewAsync(request);
        var send = await sut.SendAsync(Key(save.Document!));
        var accept = await sut.AcceptAsync(Key(send.Document!));
        Assert.True(accept.Succeeded, accept.ErrorMessage);

        _clock.Today = FixedToday.AddDays(1);

        var opened = await sut.GetAsync(save.QtNo!);
        Assert.True(opened.Succeeded, opened.ErrorMessage);
        // ACCEPTED is sticky: the status is not rewritten and ExpiredDate is not stamped.
        Assert.Equal(SaQtStatuses.Accepted, opened.Document!.Status);
        Assert.Null(opened.Document.ExpiredDate);
        Assert.True(opened.Document.IsExpired);

        var convert = await sut.ConvertToSoAsync(Key(opened.Document));

        Assert.False(convert.Succeeded);
        Assert.Contains("expired", convert.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.SaQts.SingleAsync();
        Assert.Equal(SaQtStatuses.Accepted, stored.Status);
        Assert.Null(stored.ExpiredDate);
        Assert.Empty(await db.SaSos.ToListAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Revision
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revise_supersedes_the_previous_revision_and_starts_a_new_one()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 10m, price: 12m));
        var request = QtRequest(qty: 8m, price: 15m, rowVersion: save.Document!.RowVersion);
        request.RevisionReason = "Customer renegotiated";

        var revise = await sut.ReviseAsync(save.QtNo!, request);

        Assert.True(revise.Succeeded, revise.ErrorMessage);
        Assert.Equal(2, revise.Document!.CustRel);
        Assert.True(revise.Document.IsCurrent);
        Assert.Equal(2, revise.Document.LastCustRel);
        Assert.Equal(SaQtStatuses.New, revise.Document.Status);
        Assert.Equal("Customer renegotiated", revise.Document.RevisionReason);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.SaQts.OrderBy(x => x.CustRel).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsCurrent);
        Assert.Equal(SaQtStatuses.Superseded, rows[0].Status);
        Assert.True(rows[1].IsCurrent);
        // Exactly one current revision — the filtered unique index depends on it.
        Assert.Single(rows, x => x.IsCurrent);
        Assert.Equal(2, await db.SaQtDetails.CountAsync());
    }

    [Fact]
    public async Task Revise_requires_a_reason_once_sent_or_accepted()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var send = await sut.SendAsync(Key(save.Document!));

        var noReason = await sut.ReviseAsync(
            save.QtNo!,
            QtRequest(qty: 1m, price: 10m, rowVersion: send.Document!.RowVersion));
        Assert.False(noReason.Succeeded);
        Assert.Equal(SaQtErrorKind.Validation, noReason.ErrorKind);
        Assert.True(noReason.ValidationErrors.ContainsKey("RevisionReason"));

        var withReason = QtRequest(qty: 1m, price: 10m, rowVersion: send.Document!.RowVersion);
        withReason.RevisionReason = "Price refresh";
        var revised = await sut.ReviseAsync(save.QtNo!, withReason);
        Assert.True(revised.Succeeded, revised.ErrorMessage);
        Assert.Equal(2, revised.Document!.CustRel);
    }

    [Fact]
    public async Task Revise_after_accept_resets_to_new_and_drops_the_acceptance()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);
        Assert.Equal(SaQtStatuses.Accepted, accepted.Document!.Status);

        var request = QtRequest(qty: 2m, price: 11m, rowVersion: accepted.Document.RowVersion);
        request.RevisionReason = "Customer asked for a longer validity";
        var revised = await sut.ReviseAsync(accepted.QtNo!, request);

        Assert.True(revised.Succeeded, revised.ErrorMessage);
        Assert.Equal(2, revised.Document!.CustRel);
        // Acceptance never carries forward — the new revision must be sent and accepted again.
        Assert.Equal(SaQtStatuses.New, revised.Document.Status);
        Assert.Null(revised.Document.AcceptedDate);
        Assert.Null(revised.Document.AcceptedBy);
        Assert.Null(revised.Document.SentDate);
        Assert.Equal(SaQtConversionStatuses.None, revised.Document.ConversionStatus);

        // And it cannot be converted until it has been accepted again.
        var convert = await sut.ConvertToSoAsync(Key(revised.Document));
        Assert.False(convert.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, convert.ErrorKind);
    }

    [Fact]
    public async Task Revise_is_blocked_when_a_line_has_converted_quantity()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.SaQtDetails.SingleAsync();
            detail.ConvertedQty = 0.5m;
            await db.SaveChangesAsync();
        }

        var revise = await sut.ReviseAsync(
            save.QtNo!,
            QtRequest(qty: 1m, price: 10m, rowVersion: save.Document!.RowVersion));

        Assert.False(revise.Succeeded);
        Assert.Equal(SaQtReasonCodes.ConvertedLineMessage, revise.ErrorMessage);
    }

    [Fact]
    public async Task Revise_draft_reports_the_next_revision_with_zeroed_converted_quantities()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 4m, price: 9m));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.SaQtDetails.SingleAsync();
            detail.ConvertedQty = 0m;
            await db.SaveChangesAsync();
        }

        var draft = await sut.GetReviseDraftAsync(save.QtNo!);

        Assert.True(draft.Succeeded, draft.ErrorMessage);
        Assert.Equal(2, draft.Document!.CustRel);
        Assert.Equal(SaQtStatuses.New, draft.Document.Status);
        Assert.Null(draft.Document.RevisionReason);
        var line = Assert.Single(draft.Document.Lines);
        Assert.Equal(0m, line.ConvertedQty);
        Assert.Equal(4m, line.RemainingQty);
    }

    [Fact]
    public async Task Historical_revision_cannot_be_mutated()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var revise = QtRequest(qty: 2m, price: 10m, rowVersion: save.Document!.RowVersion);
        revise.RevisionReason = "bump";
        var revised = await sut.ReviseAsync(save.QtNo!, revise);
        Assert.True(revised.Succeeded, revised.ErrorMessage);

        // A stale RowVersion + stale CustRel from revision 1 must not be able to write.
        var staleUpdate = await sut.UpdateAsync(
            save.QtNo!,
            QtRequest(qty: 3m, price: 10m, rowVersion: save.Document!.RowVersion));
        Assert.False(staleUpdate.Succeeded);
        Assert.Equal(SaQtErrorKind.Concurrency, staleUpdate.ErrorKind);

        var staleAccept = await sut.AcceptAsync(new SaQtKeyedRequest
        {
            QtNo = save.QtNo!,
            CustRel = 1,
            RowVersion = save.Document!.RowVersion
        });
        Assert.False(staleAccept.Succeeded);

        // Historical revisions stay readable.
        var historical = await sut.GetAsync(save.QtNo!, (short)1);
        Assert.True(historical.Succeeded, historical.ErrorMessage);
        Assert.Equal(SaQtStatuses.Superseded, historical.Document!.Status);
        Assert.False(historical.Document.IsCurrent);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Conversion
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_requires_accepted_status()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var convert = await sut.ConvertToSoAsync(Key(save.Document!));

        Assert.False(convert.Succeeded);
        Assert.Equal(SaQtReasonCodes.NotAcceptedMessage, convert.ErrorMessage);
    }

    [Fact]
    public async Task Convert_creates_one_sales_order_with_source_stamps_and_closes_the_quotation()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut, qty: 3m, price: 21.25m);

        var convert = await sut.ConvertToSoAsync(Key(accepted.Document!));

        Assert.True(convert.Succeeded, convert.ErrorMessage);
        Assert.Equal("SO2609-0001", convert.ConvertedSoNo);

        await using var db = await _factory.CreateDbContextAsync();

        var qt = await db.SaQts.Include(x => x.Details).SingleAsync(x => x.IsCurrent);
        Assert.Equal(SaQtStatuses.Closed, qt.Status);
        Assert.Equal(SaQtConversionStatuses.Full, qt.ConversionStatus);
        Assert.Equal(SaQtClosedReasons.Converted, qt.ClosedReason);
        Assert.NotNull(qt.ClosedDate);
        Assert.Equal("admin", qt.ClosedBy);
        // MVP converts in full: every line is consumed and PARTIAL is never produced.
        Assert.All(qt.Details, d => Assert.Equal(d.OrderQty, d.ConvertedQty));
        Assert.Equal(3m, qt.Details.Single().ConvertedQty);

        var so = await db.SaSos.Include(x => x.Details).SingleAsync();
        Assert.Equal(SaSoStatuses.New, so.Status);
        Assert.Equal(accepted.QtNo, so.QtNo);
        Assert.Equal((short?)accepted.Document.CustRel, so.QtCustRel);
        Assert.Equal(accepted.Document.TotAmnt, so.TotAmnt);

        var soLine = so.Details.Single();
        Assert.Equal(accepted.QtNo, soLine.QtNo);
        Assert.Equal((short)1, soLine.QtLine);
        Assert.Equal((short?)accepted.Document.CustRel, soLine.QtCustRel);
        Assert.Equal(3m, soLine.QtConsumedQty);
        Assert.Equal(3m, soLine.OrderQty);
        Assert.Equal(3m, soLine.BalanceQty);
        Assert.Equal(0m, soLine.ShippedQty);
    }

    [Fact]
    public async Task Convert_copies_the_quotation_snapshot_and_never_reprices()
    {
        var sut = CreateQtSut();
        // The operator quoted 21.25 for an item whose master price is 50.00.
        var accepted = await CreateAcceptedAsync(sut, qty: 2m, price: 21.25m, iCode: "SVC1");

        // Both masters move after the quotation was agreed.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "SVC1");
            item.SellingPrice = 999m;
            var tax = await db.SaTaxGroups.SingleAsync(x => x.TaxGrCode == "SR");
            tax.Percentage = 12m;
            await db.SaveChangesAsync();
        }

        var convert = await sut.ConvertToSoAsync(Key(accepted.Document!));
        Assert.True(convert.Succeeded, convert.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var so = await verify.SaSos.Include(x => x.Details).SingleAsync();
        var quoteLine = accepted.Document!.Lines.Single();
        var soLine = so.Details.Single();

        Assert.Equal(21.25m, soLine.UnitPrice);
        Assert.Equal(quoteLine.Amount, soLine.Amount);
        Assert.Equal(quoteLine.NetAmount, soLine.NetAmount);
        Assert.Equal(quoteLine.TaxAmt, soLine.TaxAmt);
        Assert.Equal(quoteLine.LocalAmount, soLine.LocalAmount);
        Assert.Equal(quoteLine.ItemDiscount, soLine.ItemDiscount);
        Assert.Equal(accepted.Document.TotAmnt, so.TotAmnt);
        Assert.Equal(accepted.Document.GrossAmnt, so.GrossAmnt);
        Assert.Equal(accepted.Document.Taxes, so.Taxes);
        // Provenance travels with the frozen price.
        Assert.Equal(quoteLine.PricingSource, soLine.PricingSource);
        Assert.Equal(quoteLine.PricingRef, soLine.PricingRef);
    }

    [Fact]
    public async Task Convert_is_rejected_when_conversion_status_is_not_none()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var qt = await db.SaQts.SingleAsync();
            qt.ConversionStatus = SaQtConversionStatuses.Partial;
            await db.SaveChangesAsync();
        }

        var reloaded = await sut.GetAsync(accepted.QtNo!);
        var convert = await sut.ConvertToSoAsync(Key(reloaded.Document!));

        Assert.False(convert.Succeeded);
        Assert.Equal(SaQtReasonCodes.AlreadyConvertedMessage, convert.ErrorMessage);
    }

    [Fact]
    public async Task Convert_is_rejected_when_any_line_has_converted_quantity()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.SaQtDetails.SingleAsync();
            detail.ConvertedQty = 0.25m;
            await db.SaveChangesAsync();
        }

        var reloaded = await sut.GetAsync(accepted.QtNo!);
        var convert = await sut.ConvertToSoAsync(Key(reloaded.Document!));

        Assert.False(convert.Succeeded);
        Assert.Equal(SaQtReasonCodes.AlreadyConvertedMessage, convert.ErrorMessage);
    }

    [Fact]
    public async Task Second_convert_of_the_same_revision_is_rejected()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);
        var first = await sut.ConvertToSoAsync(Key(accepted.Document!));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var second = await sut.ConvertToSoAsync(Key(accepted.Document!));

        Assert.False(second.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.SaSos.CountAsync());
    }

    [Fact]
    public async Task Duplicate_convert_attempt_with_a_stale_key_creates_no_second_sales_order()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);
        var staleKey = Key(accepted.Document!);
        Assert.True((await sut.ConvertToSoAsync(staleKey)).Succeeded);

        // A second caller holding the same (now stale) RowVersion and status sees the same rejection a
        // concurrent caller would see after the first conversion committed. The real parallel race is
        // covered against SQL Server in SaQtSqlServerConcurrencyTests, because SQLite cannot express
        // UPDLOCK/HOLDLOCK or the filtered unique index.
        var second = await sut.ConvertToSoAsync(staleKey);

        Assert.False(second.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.SaSos.CountAsync());
        var qt = await db.SaQts.SingleAsync(x => x.IsCurrent);
        Assert.Equal(SaQtStatuses.Closed, qt.Status);
        Assert.Equal(SaQtConversionStatuses.Full, qt.ConversionStatus);
    }

    [Fact]
    public async Task Failed_convert_rolls_back_the_sales_order_and_leaves_the_quotation_alone()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);

        // The SO insert succeeds, then the close step is forced to fail: the whole conversion must
        // roll back, so a partially converted quotation can never be observed.
        var converter = new SaQtService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            Access().Object,
            new QtTestNumberingService(),
            _clock,
            new SaQtRepository(),
            new SaCustRepository(_factory),
            CreateSoService(Access().Object, new QtTestNumberingService()),
            new FakeAppSettingService(),
            NullLogger<SaQtService>.Instance)
        {
            TestHookAfterSoInsertBeforeClose = () =>
                throw new InvalidOperationException("forced conversion failure")
        };

        var convert = await converter.ConvertToSoAsync(Key(accepted.Document!));

        Assert.False(convert.Succeeded);
        Assert.Equal(SaQtErrorKind.Unexpected, convert.ErrorKind);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.SaSos.ToListAsync());
        var qt = await db.SaQts.Include(x => x.Details).SingleAsync(x => x.IsCurrent);
        Assert.Equal(SaQtStatuses.Accepted, qt.Status);
        Assert.Equal(SaQtConversionStatuses.None, qt.ConversionStatus);
        Assert.Null(qt.ClosedReason);
        Assert.All(qt.Details, d => Assert.Equal(0m, d.ConvertedQty));
    }

    [Fact]
    public async Task Converted_quotation_can_still_be_revised_because_it_is_closed_not_converted()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut);
        var convert = await sut.ConvertToSoAsync(Key(accepted.Document!));
        Assert.True(convert.Succeeded, convert.ErrorMessage);

        var opened = await sut.GetAsync(accepted.QtNo!);
        Assert.Equal(SaQtStatuses.Closed, opened.Document!.Status);
        Assert.Equal("SO2609-0001", opened.Document.ConvertedSoNo);

        // CLOSED is not a revisable status: the revision path must refuse.
        var revise = await sut.ReviseAsync(
            accepted.QtNo!,
            QtRequest(qty: 1m, price: 1m, rowVersion: opened.Document.RowVersion));
        Assert.False(revise.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, revise.ErrorKind);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Edit / delete
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_is_only_allowed_on_new_and_requires_the_current_row_version()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var good = await sut.UpdateAsync(
            save.QtNo!,
            QtRequest(qty: 5m, price: 10m, rowVersion: save.Document!.RowVersion));
        Assert.True(good.Succeeded, good.ErrorMessage);
        Assert.Equal(5m, Assert.Single(good.Document!.Lines).OrderQty);

        var stale = await sut.UpdateAsync(
            save.QtNo!,
            QtRequest(qty: 6m, price: 10m, rowVersion: save.Document.RowVersion));
        Assert.False(stale.Succeeded);
        Assert.Equal(SaQtErrorKind.Concurrency, stale.ErrorKind);

        var send = await sut.SendAsync(Key(good.Document));
        var afterSend = await sut.UpdateAsync(
            save.QtNo!,
            QtRequest(qty: 7m, price: 10m, rowVersion: send.Document!.RowVersion));
        Assert.False(afterSend.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, afterSend.ErrorKind);
    }

    [Fact]
    public async Task Delete_removes_a_new_quotation_and_its_lines()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var delete = await sut.DeleteAsync([Key(save.Document!)]);

        Assert.True(delete.Succeeded, delete.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.SaQts.ToListAsync());
        Assert.Empty(await db.SaQtDetails.ToListAsync());
    }

    [Fact]
    public async Task Delete_is_refused_once_the_quotation_has_left_new()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var send = await sut.SendAsync(Key(save.Document!));

        var delete = await sut.DeleteAsync([Key(send.Document!)]);

        Assert.False(delete.Succeeded);
        Assert.Equal(SaQtErrorKind.BusinessRule, delete.ErrorKind);
    }

    [Fact]
    public async Task Deleting_a_revision_restores_the_previous_one_as_new()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var revise = QtRequest(qty: 2m, price: 10m, rowVersion: save.Document!.RowVersion);
        revise.RevisionReason = "bump";
        var revised = await sut.ReviseAsync(save.QtNo!, revise);
        Assert.True(revised.Succeeded, revised.ErrorMessage);

        var delete = await sut.DeleteAsync([Key(revised.Document!)]);

        Assert.True(delete.Succeeded, delete.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaQts.SingleAsync();
        Assert.Equal(1, row.CustRel);
        Assert.True(row.IsCurrent);
        Assert.Equal(SaQtStatuses.New, row.Status);
    }

    [Fact]
    public async Task Failed_revise_leaves_the_original_revision_current()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        var revising = new SaQtService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            Access().Object,
            new QtTestNumberingService(),
            _clock,
            new SaQtRepository(),
            new SaCustRepository(_factory),
            CreateSoService(Access().Object, new QtTestNumberingService()),
            new FakeAppSettingService(),
            NullLogger<SaQtService>.Instance)
        {
            TestHookAfterSupersedeBeforeInsert = () =>
                throw new InvalidOperationException("forced revise failure")
        };

        var revise = QtRequest(qty: 9m, price: 10m, rowVersion: save.Document!.RowVersion);
        revise.RevisionReason = "will fail";
        var result = await revising.ReviseAsync(save.QtNo!, revise);

        Assert.False(result.Succeeded);
        Assert.Equal(SaQtErrorKind.Unexpected, result.ErrorKind);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.SaQts.ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].IsCurrent);
        Assert.Equal(1, rows[0].CustRel);
        Assert.Equal(1, rows[0].LastCustRel);
        Assert.Equal(SaQtStatuses.New, rows[0].Status);
    }

    [Fact]
    public async Task Expired_quotation_cannot_be_converted()
    {
        var sut = CreateQtSut();
        var request = QtRequest(qty: 1m, price: 10m);
        request.QtDate = FixedToday.AddDays(-10);
        request.ValidUntil = FixedToday.AddDays(-1);
        var save = await sut.SaveNewAsync(request);
        var send = await sut.SendAsync(Key(save.Document!));
        Assert.False(send.Succeeded);

        var opened = await sut.GetAsync(save.QtNo!);
        Assert.Equal(SaQtStatuses.Expired, opened.Document!.Status);

        var convert = await sut.ConvertToSoAsync(Key(opened.Document!));

        Assert.False(convert.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.SaSos.ToListAsync());
    }

    [Fact]
    public async Task Every_lifecycle_mutation_advances_the_row_version()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var v1 = save.Document!.RowVersion;

        var send = await sut.SendAsync(Key(save.Document!));
        Assert.True(send.Succeeded, send.ErrorMessage);
        Assert.NotEqual(v1, send.Document!.RowVersion);

        var accept = await sut.AcceptAsync(Key(send.Document!));
        Assert.True(accept.Succeeded, accept.ErrorMessage);
        Assert.NotEqual(send.Document.RowVersion, accept.Document!.RowVersion);

        var convert = await sut.ConvertToSoAsync(Key(accept.Document!));
        Assert.True(convert.Succeeded, convert.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var current = await db.SaQts.SingleAsync(x => x.IsCurrent);
        Assert.NotEqual(accept.Document.RowVersion, current.RowVersion);

        // Revision generation also replaces the concurrency token.
        var save2 = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var revise = QtRequest(qty: 2m, price: 10m, rowVersion: save2.Document!.RowVersion);
        revise.RevisionReason = "bump";
        var revised = await sut.ReviseAsync(save2.QtNo!, revise);
        Assert.True(revised.Succeeded, revised.ErrorMessage);
        Assert.NotEqual(save2.Document.RowVersion, revised.Document!.RowVersion);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Listing / tenant scope
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_returns_the_current_revision_only()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        var revise = QtRequest(qty: 2m, price: 10m, rowVersion: save.Document!.RowVersion);
        revise.RevisionReason = "bump";
        await sut.ReviseAsync(save.QtNo!, revise);

        var page = await sut.SearchAsync(new SaQtListQuery());

        Assert.True(page.Succeeded, page.ErrorMessage);
        var row = Assert.Single(page.ListPage!.Rows);
        Assert.Equal(2, row.CustRel);
        Assert.Equal(1, page.ListPage.TotalCount);
        Assert.True(row.CanRevise);
        Assert.False(row.CanConvert);
    }

    [Fact]
    public async Task Search_action_hints_track_the_lifecycle()
    {
        var sut = CreateQtSut();
        var accepted = await CreateAcceptedAsync(sut, qty: 1m, price: 10m);

        var page = await sut.SearchAsync(new SaQtListQuery());
        var row = Assert.Single(page.ListPage!.Rows);

        Assert.True(row.CanConvert);
        // Losing / cancelling stops making sense once the customer has accepted.
        Assert.False(row.CanLose);
        Assert.False(row.CanCancel);
        Assert.False(row.CanEdit);
        Assert.False(row.CanSend);
        Assert.False(row.CanAccept);
        Assert.False(row.CanDelete);

        var convert = await sut.ConvertToSoAsync(Key(accepted.Document!));
        Assert.True(convert.Succeeded, convert.ErrorMessage);

        var after = await sut.SearchAsync(new SaQtListQuery());
        var closed = Assert.Single(after.ListPage!.Rows);
        Assert.Equal(SaQtStatuses.Closed, closed.Status);
        Assert.Equal("SO2609-0001", closed.ConvertedSoNo);
        Assert.False(closed.CanConvert);
        Assert.False(closed.CanRevise);
    }

    [Fact]
    public async Task Quotations_are_scoped_to_the_company_and_branch()
    {
        var sut = CreateQtSut();
        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var otherTenant = new SaQtService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company: "OTHER", branch: "HQ", location: "SITE"),
            Access().Object,
            new QtTestNumberingService(),
            _clock,
            new SaQtRepository(),
            new SaCustRepository(_factory),
            CreateSoService(Access().Object, new QtTestNumberingService()),
            new FakeAppSettingService(),
            NullLogger<SaQtService>.Instance);

        var other = await otherTenant.GetAsync(save.QtNo!);
        Assert.False(other.Succeeded);
        Assert.Equal(SaQtErrorKind.NotFound, other.ErrorKind);

        var page = await otherTenant.SearchAsync(new SaQtListQuery());
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Empty(page.ListPage!.Rows);
    }

    [Fact]
    public async Task Permission_is_required_for_every_write()
    {
        var denied = new SaQtService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            DenyPermission(PermissionCodes.Add).Object,
            new QtTestNumberingService(),
            _clock,
            new SaQtRepository(),
            new SaCustRepository(_factory),
            CreateSoService(Access().Object, new QtTestNumberingService()),
            new FakeAppSettingService(),
            NullLogger<SaQtService>.Instance);

        var save = await denied.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        Assert.False(save.Succeeded);
        Assert.Equal(SaQtErrorKind.Authorization, save.ErrorKind);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static SaQtKeyedRequest Key(SaQtDocument document) =>
        new()
        {
            QtNo = document.QtNo,
            CustRel = document.CustRel,
            RowVersion = document.RowVersion
        };

    private async Task<SaQtOperationResult> CreateAcceptedAsync(
        SaQtService sut,
        decimal qty = 1m,
        decimal price = 10m,
        string iCode = "SVC1")
    {
        var save = await sut.SaveNewAsync(QtRequest(qty, price, iCode: iCode));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var send = await sut.SendAsync(Key(save.Document!));
        Assert.True(send.Succeeded, send.ErrorMessage);

        var accept = await sut.AcceptAsync(Key(send.Document!));
        Assert.True(accept.Succeeded, accept.ErrorMessage);
        return accept;
    }

    // ── P5b: the validity window now comes from SALES.QUOTE_VALID_DAYS ─────────

    /// <summary>
    /// The setting supplies the DEFAULT window only. An explicit ValidUntil on the request is still
    /// honoured, so a caller can always name a date of its own.
    /// </summary>
    [Fact]
    public async Task A_new_quotation_uses_the_company_validity_window()
    {
        const string module = ErpWeb.Core.Settings.AppSettingModules.Sales;
        const string key = ErpWeb.Core.Settings.AppSettingCatalogue.SalesKeys.QuoteValidDays;

        var settings = new FakeAppSettingService().With(module, key, "45");
        var sut = CreateQtSut(settings: settings);

        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(FixedToday.AddDays(45), save.Document!.ValidUntil);
        Assert.True(settings.ReadCount > 0, "the validity window must be read from the settings service");
    }

    /// <summary>
    /// The P5b acceptance condition: with no setting configured the behaviour must be byte-identical to
    /// what it was before the setting existed.
    /// </summary>
    [Fact]
    public async Task A_new_quotation_without_the_setting_keeps_the_shipped_window()
    {
        var sut = CreateQtSut();   // the fake resolves nothing at all

        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(FixedToday.AddDays(SaQtValidity.DefaultValidityDays), save.Document!.ValidUntil);
    }

    /// <summary>
    /// A nonsense window must never produce a quotation that is already expired, so zero, negative and
    /// unparseable values all fall back to the shipped default rather than being obeyed.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public async Task A_nonsense_validity_setting_keeps_the_shipped_window(string configured)
    {
        const string module = ErpWeb.Core.Settings.AppSettingModules.Sales;
        const string key = ErpWeb.Core.Settings.AppSettingCatalogue.SalesKeys.QuoteValidDays;

        var sut = CreateQtSut(settings: new FakeAppSettingService().With(module, key, configured));

        var save = await sut.SaveNewAsync(QtRequest(qty: 1m, price: 10m));

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(FixedToday.AddDays(SaQtValidity.DefaultValidityDays), save.Document!.ValidUntil);
    }

    private SaQtService CreateQtSut(
        Mock<IAccessRightService>? access = null,
        ErpWeb.Core.Settings.IAppSettingService? settings = null)
    {
        var accessRights = (access ?? Access()).Object;
        var numbering = new QtTestNumberingService();
        return new SaQtService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            accessRights,
            numbering,
            _clock,
            new SaQtRepository(),
            new SaCustRepository(_factory),
            CreateSoService(accessRights, numbering),
            settings ?? new FakeAppSettingService(),
            NullLogger<SaQtService>.Instance);
    }

    private SaSoService CreateSoService(IAccessRightService access, IDocumentNumberingService numbering) =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access,
            numbering,
            _clock,
            new SaSoRepository(),
            new SaCustRepository(_factory),
            new SaDocApplicationService(new SaSoRepository(), new SaDoRepository()),
            NullLogger<SaSoService>.Instance);

    private static SaQtSaveRequest QtRequest(
        decimal qty,
        decimal price,
        string cust = "CUST01",
        string iCode = "SVC1",
        byte[]? rowVersion = null,
        int line = 0) =>
        new()
        {
            QtDate = FixedToday,
            CustCode = cust,
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "RFQ-1",
            SalesRep = "SM1",
            RowVersion = rowVersion,
            Lines = [QtLine(iCode, qty, price, line)]
        };

    private static SaQtLineRequest QtLine(string iCode, decimal qty, decimal price, int line = 0) =>
        new()
        {
            Line = line,
            ICode = iCode,
            OrderQty = qty,
            UnitPrice = price
        };

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

    /// <summary>A clock the test can move, so expiry boundaries are exercised without sleeping.</summary>
    private sealed class MutableCurrentDateService : ICurrentDateService
    {
        public MutableCurrentDateService(DateTime today) => Today = today.Date;

        public DateTime Today { get; set; }
        public DateTime Now => Today;
    }
}

/// <summary>
/// Numbering fake covering both documents a conversion touches: the quotation itself (<c>QT</c>) and
/// the Sales Order it produces (<c>SO</c>).
/// </summary>
internal sealed class QtTestNumberingService : IDocumentNumberingService
{
    private int _qtSeq;
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

        if (string.Equals(module, "QT", StringComparison.OrdinalIgnoreCase))
        {
            var n = Interlocked.Increment(ref _qtSeq);
            return Task.FromResult(new DocumentNumberResult($"QT{documentDate:yy}{documentDate:MM}-{n:D4}", "QT"));
        }

        var so = Interlocked.Increment(ref _soSeq);
        return Task.FromResult(new DocumentNumberResult($"SO{documentDate:yy}{documentDate:MM}-{so:D4}", "SO"));
    }
}
