using ErpWeb.Core.EInvoice;
using ErpWeb.UI.Components.Common;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.JSInterop;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// <see cref="EInvoicePortalLinkOpener"/> — the shared E-UUID click behaviour.
///
/// <para>
/// These tests exist for one property above all others: the click performs <b>at most one</b> history
/// repair and <b>at most one</b> link retry, and the repair can never stop the portal tab from opening.
/// The retry is a link-resolution fallback; the tempting "fix" of looping until the link appears would
/// turn a single click into repeated MyInvois calls, so the invariant is pinned here rather than left to
/// a code comment.
/// </para>
/// </summary>
public class EInvoicePortalLinkOpenerTests
{
    private const string Uuid = "UUID-INV-1001";

    // ─────────────────────────────────── fakes ───────────────────────────────────

    private sealed class Js
    {
        public int ImportCount { get; private set; }

        public int OpenCount { get; private set; }

        public bool BlockPopup { get; set; }

        public Exception? ImportThrows { get; set; }

        public IJSRuntime Runtime { get; }

        public Js()
        {
            var module = new Mock<IJSObjectReference>();
            module
                .Setup(x => x.InvokeAsync<bool>(It.IsAny<string>(), It.IsAny<object?[]>()))
                .ReturnsAsync(() =>
                {
                    OpenCount++;
                    return !BlockPopup;
                });

            var js = new Mock<IJSRuntime>();
            js.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
                .ReturnsAsync(() =>
                {
                    ImportCount++;
                    if (ImportThrows is not null)
                    {
                        throw ImportThrows;
                    }

                    return module.Object;
                });

            Runtime = js.Object;
        }
    }

    /// <summary>Counts the repair calls and lets a test make the repair throw or refuse.</summary>
    private static Mock<ISaEInvoiceService> Service(
        Func<SaEInvoicePortalLink?>? link = null,
        Func<SaEInvoiceSubmissionRepairResult>? repair = null,
        List<int>? repairCalls = null,
        List<int>? linkCalls = null)
    {
        var service = new Mock<ISaEInvoiceService>();

        service
            .Setup(x => x.GetPortalLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                linkCalls?.Add(1);
                return link?.Invoke();
            });

        service
            .Setup(x => x.RepairSubmissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                repairCalls?.Add(1);
                return repair?.Invoke() ?? Repaired(EInvoiceHistoryWriteResult.Updated);
            });

        return service;
    }

    private static SaEInvoicePortalLink Link() =>
        SaEInvoicePortalLink.Create(Uuid, "LONG-1", EInvoiceStatuses.Valid, "https://portal.test")!;

    private static SaEInvoiceSubmissionRepairResult Repaired(
        EInvoiceHistoryWriteResult write,
        bool succeeded = true) =>
        new()
        {
            Attempted = true,
            Succeeded = succeeded,
            Message = "Repair message.",
            DocumentType = EInvoiceDocumentTypes.Invoice,
            DocumentNo = "INV-1001",
            Status = EInvoiceStatuses.Valid,
            HistoryWrite = write
        };

    private static SaEInvoiceSubmissionRepairResult NotAttempted() =>
        SaEInvoiceSubmissionRepairResult.NotApplicable("Not yours to repair.");

    // ─────────────────────────────────── basics ───────────────────────────────────

    [Fact]
    public async Task A_blank_uuid_opens_nothing_and_never_repairs()
    {
        var repairCalls = new List<int>();
        var service = Service(repairCalls: repairCalls);
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, "   ");

        Assert.False(result.Opened);
        Assert.False(result.StateChanged);
        Assert.Contains("no MyInvois UUID", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repairCalls);
        Assert.Equal(0, js.ImportCount);
    }

    [Fact]
    public async Task A_uuid_column_click_is_recognised_by_field_name_only()
    {
        var info = new SelectedColumnInfo
        {
            Fieldname = EInvoicePortalLinkOpener.UuidFieldName,
            Value = Uuid
        };

        Assert.True(EInvoicePortalLinkOpener.IsUuidColumn(info));
        Assert.False(EInvoicePortalLinkOpener.IsUuidColumn(new SelectedColumnInfo
        {
            Fieldname = "IrbmStatus",
            Value = "VALID"
        }));
        Assert.False(EInvoicePortalLinkOpener.IsUuidColumn(null));
    }

    // ─────────────────────────── the normal path (open, then repair) ───────────────────────────

    [Fact]
    public async Task The_tab_opens_and_the_repair_runs_behind_it()
    {
        var repairCalls = new List<int>();
        var service = Service(link: Link, repairCalls: repairCalls);
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.True(result.StateChanged);

        // The link message is kept, and the repair's own sentence is appended rather than replacing it.
        Assert.Contains("Opened this", result.Message, StringComparison.Ordinal);
        Assert.Contains("Repair message.", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, repairCalls.Count);
    }

    [Fact]
    public async Task StateChanged_is_true_when_only_the_document_status_moved()
    {
        // Succeeded means "a MyInvois status was applied", which alone can move IRBMStatus — and IRBMStatus
        // is what the grid's E-Inv / E-Status columns show. So a skipped HISTORY write is not enough to
        // declare the page up to date.
        var service = Service(link: Link, repair: () => Repaired(EInvoiceHistoryWriteResult.Skipped));
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.True(result.StateChanged);
    }

    [Fact]
    public async Task StateChanged_is_false_when_nothing_was_applied_and_no_row_was_written()
    {
        // MyInvois could not be read AND the document carries no submission id: no status moved and no row
        // exists to write. Nothing on this page can have changed, so it must not reload.
        var service = Service(
            link: Link,
            repair: () => Repaired(EInvoiceHistoryWriteResult.Skipped, succeeded: false));
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.False(result.StateChanged);
    }

    [Fact]
    public async Task StateChanged_is_false_when_the_repair_was_not_attempted()
    {
        var service = Service(link: Link, repair: NotAttempted);
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.False(result.StateChanged);
        Assert.Contains("Not yours to repair.", result.Message, StringComparison.Ordinal);

        // The 4-line handler contract: Opened still reflects the LINK, not the repair.
    }

    // ─────────────────────────── best-effort: the repair must never block ───────────────────────────

    [Fact]
    public async Task The_tab_still_opens_when_the_repair_throws()
    {
        var service = new Mock<ISaEInvoiceService>();
        service
            .Setup(x => x.GetPortalLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Link);
        service
            .Setup(x => x.RepairSubmissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("MyInvois is down."));

        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.False(result.StateChanged);
        Assert.DoesNotContain("MyInvois is down.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_popup_is_reported_but_the_repair_still_runs()
    {
        var repairCalls = new List<int>();
        var service = Service(link: Link, repairCalls: repairCalls);
        var js = new Js { BlockPopup = true };

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.False(result.Opened);
        Assert.Contains("blocked the new tab", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, repairCalls.Count);
    }

    [Fact]
    public async Task OpenAsync_keeps_its_original_behaviour_and_never_repairs()
    {
        var repairCalls = new List<int>();
        var service = Service(link: Link, repairCalls: repairCalls);
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.Equal("Opened this VALID e-Invoice at the LHDN MyInvois portal.", result.Message);
        Assert.False(result.StateChanged);
        Assert.Empty(repairCalls);
    }

    // ─────────────────────── the fallback: repair once, re-resolve once ───────────────────────

    [Fact]
    public async Task The_link_is_retried_once_after_a_successful_repair()
    {
        // First resolve finds nothing (the row was never written); the repair creates it; the retry then
        // builds the link. This is the case the whole feature exists for.
        var resolved = 0;
        var repairCalls = new List<int>();
        var service = new Mock<ISaEInvoiceService>();
        service
            .Setup(x => x.GetPortalLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++resolved == 1 ? null : Link());
        service
            .Setup(x => x.RepairSubmissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                repairCalls.Add(1);
                return Repaired(EInvoiceHistoryWriteResult.Inserted);
            });

        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.True(result.Opened);
        Assert.True(result.StateChanged);
        Assert.Equal(2, resolved);
        Assert.Equal(1, repairCalls.Count);
    }

    [Fact]
    public async Task The_repair_runs_at_most_once_when_the_link_stays_unresolvable()
    {
        // THE invariant. The link never resolves, so nothing may drive a second repair: one MyInvois read
        // per click is the contract, and a future "retry until it opens" change must fail here.
        var resolved = 0;
        var repairCalls = new List<int>();
        var service = new Mock<ISaEInvoiceService>();
        service
            .Setup(x => x.GetPortalLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                resolved++;
                return null;
            });
        service
            .Setup(x => x.RepairSubmissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                repairCalls.Add(1);
                return Repaired(EInvoiceHistoryWriteResult.Skipped, succeeded: false);
            });

        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.False(result.Opened);
        Assert.Contains("No LHDN link", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, repairCalls.Count);
        Assert.Equal(2, resolved);

        // Nothing was opened, and the failure is reported through the link message.
        Assert.Equal(0, js.ImportCount);
    }

    [Fact]
    public async Task A_resolve_failure_is_reported_without_spending_a_repair()
    {
        // A throwing lookup is a broken data layer, not a missing row: repairing it would just repeat the
        // same failure, so the original message is preserved verbatim.
        var repairCalls = new List<int>();
        var service = new Mock<ISaEInvoiceService>();
        service
            .Setup(x => x.GetPortalLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("history table is gone"));
        service
            .Setup(x => x.RepairSubmissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                repairCalls.Add(1);
                return Repaired(EInvoiceHistoryWriteResult.Inserted);
            });

        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.False(result.Opened);
        Assert.Equal("history table is gone", result.Message);
        Assert.Empty(repairCalls);
    }

    [Fact]
    public async Task An_unresolvable_link_reports_both_halves_of_the_failure()
    {
        // The repair refused (no permission), and the link is still unbuildable: the operator needs to see
        // both, because either one alone sends them down the wrong path.
        var service = Service(link: () => null, repair: NotAttempted);
        var js = new Js();

        var result = await EInvoicePortalLinkOpener.OpenAndRepairAsync(service.Object, js.Runtime, Uuid);

        Assert.False(result.Opened);
        Assert.Contains("No LHDN link", result.Message, StringComparison.Ordinal);
        Assert.Contains("Not yours to repair.", result.Message, StringComparison.Ordinal);
    }

    // ───────────────── INVALID routes to the detail page ─────────────────

    private static SelectedColumnInfo UuidCell(string? uuid = Uuid) => new()
    {
        Fieldname = EInvoicePortalLinkOpener.UuidFieldName,
        Value = uuid
    };

    [Theory]
    [InlineData("INVALID")]
    [InlineData("Invalid")]
    [InlineData("invalid")]
    public async Task Any_casing_of_invalid_routes_to_the_detail_page(string status)
    {
        // Legacy rows hold title-case literals, ErpWeb writes uppercase ones: all of them must route.
        var linkCalls = new List<int>();
        var repairCalls = new List<int>();
        var service = Service(linkCalls: linkCalls, repairCalls: repairCalls);
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object, js.Runtime, UuidCell(), status, hasDetailAccess: true);

        Assert.Equal($"/sales/einvoice/detail/{Uuid}", outcome.NavigateUrl);
        Assert.Null(outcome.Portal);

        // Routing is free: no portal lookup, no repair, no tab, no script import.
        Assert.Empty(linkCalls);
        Assert.Empty(repairCalls);
        Assert.Equal(0, js.ImportCount);
        Assert.Equal(0, js.OpenCount);
    }

    [Fact]
    public async Task A_valid_document_still_opens_the_portal()
    {
        var service = Service(link: Link);
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object, js.Runtime, UuidCell(), EInvoiceStatuses.Valid, hasDetailAccess: true);

        Assert.Null(outcome.NavigateUrl);
        Assert.True(outcome.Portal!.Opened);
        Assert.Equal(1, js.OpenCount);
    }

    [Fact]
    public async Task A_cancelled_document_still_uses_the_portal()
    {
        var service = Service(link: Link);
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object, js.Runtime, UuidCell(), EInvoiceStatuses.Cancelled, hasDetailAccess: true);

        Assert.Null(outcome.NavigateUrl);
        Assert.NotNull(outcome.Portal);
    }

    [Fact]
    public async Task A_null_status_is_not_treated_as_invalid()
    {
        var service = Service(link: Link);
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object, js.Runtime, UuidCell(), null, hasDetailAccess: true);

        Assert.Null(outcome.NavigateUrl);
        Assert.NotNull(outcome.Portal);
    }

    [Fact]
    public async Task An_invalid_document_without_detail_access_opens_nothing_and_says_so()
    {
        // Navigating would bounce the operator to /unauthorized, and the portal path could only answer
        // "No LHDN link" for an INVALID document, so neither is offered.
        var linkCalls = new List<int>();
        var repairCalls = new List<int>();
        var service = Service(linkCalls: linkCalls, repairCalls: repairCalls);
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object, js.Runtime, UuidCell(), EInvoiceStatuses.Invalid, hasDetailAccess: false);

        Assert.Null(outcome.NavigateUrl);
        Assert.Null(outcome.Portal);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
        Assert.Empty(linkCalls);
        Assert.Empty(repairCalls);
        Assert.Equal(0, js.OpenCount);
    }

    [Fact]
    public async Task An_invalid_document_with_no_uuid_reports_instead_of_navigating()
    {
        var service = Service();
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object, js.Runtime, UuidCell(uuid: null), EInvoiceStatuses.Invalid, hasDetailAccess: true);

        Assert.Null(outcome.NavigateUrl);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
    }

    [Fact]
    public async Task A_non_uuid_column_is_ignored()
    {
        var service = Service();
        var js = new Js();

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            service.Object,
            js.Runtime,
            new SelectedColumnInfo { Fieldname = "IrbmStatus", Value = "INVALID" },
            EInvoiceStatuses.Invalid,
            hasDetailAccess: true);

        Assert.True(outcome.Ignored);
        Assert.Equal(0, js.OpenCount);
    }
}
