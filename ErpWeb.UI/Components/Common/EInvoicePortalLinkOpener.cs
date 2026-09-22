using ErpWeb.Core.EInvoice;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Components.Common;

/// <summary>
/// The <b>standard</b> click behaviour for a grid's e-Invoice UUID cell, shared by every screen that
/// shows a MyInvois UUID (sales invoice today; credit/debit note and the self-billed families later).
///
/// <para>
/// A page only needs the row to expose an <c>IrbmUuid</c> string, the column to be declared
/// <c>DataType = "link"</c>, and this four-line handler:
/// </para>
/// <code>
/// protected async Task onSelectColHandle(SelectedColumnInfo info)
/// {
///     var row = info.Context as SaInvoiceListRow;
///
///     var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
///         EInvoices, JsRuntime, info, row?.IrbmStatus, CanAccessEInvoiceTin);
///
///     if (outcome.NavigateUrl is not null) { Navigation.NavigateTo(outcome.NavigateUrl); return; }
///     if (outcome.Message is not null) { ErrorMessage = outcome.Message; return; }
///
///     var result = outcome.Portal!;
///     if (result.Opened) { ErrorMessage = null; StatusMessage = result.Message; }
///     else { ErrorMessage = result.Message; }
///     if (result.StateChanged) { await ReloadGridAsync(); }
/// }
/// </code>
///
/// <para>
/// An <b>INVALID</b> document is routed to the LHDN detail page instead of the portal. See
/// <see cref="HandleUuidClickAsync"/> for why.
/// </para>
///
/// <para>
/// All the rules live behind <see cref="ISaEInvoiceService.GetPortalLinkAsync"/> (submission lookup,
/// company scoping and the VALID/CANCELLED rule) and
/// <see cref="ISaEInvoiceService.RepairSubmissionAsync"/> (re-read MyInvois and create-or-update the
/// submission history), so no page repeats them. Use <see cref="OpenAsync"/> only where the history
/// repair is explicitly unwanted.
/// </para>
/// </summary>
public static class EInvoicePortalLinkOpener
{
    /// <summary>
    /// The field name a list row must expose for <see cref="IsUuidColumn"/> to recognise the cell. It
    /// matches the <c>IRBMUUID</c> property name used by the sales document rows.
    /// </summary>
    public const string UuidFieldName = "IrbmUuid";

    private const string ModulePath = "/js/einvoice-portal.js";

    private const string NoUuidMessage = "This document has no MyInvois UUID yet.";

    private const string NoLinkMessage =
        "No LHDN link for this document. Only a VALID or CANCELLED e-Invoice with a MyInvois "
        + "long id can be opened at the portal.";

    /// <summary>True when the grid cell that was clicked is the e-Invoice UUID column.</summary>
    public static bool IsUuidColumn(SelectedColumnInfo? info) =>
        info is not null && string.Equals(info.Fieldname, UuidFieldName, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the LHDN portal link for <paramref name="uuid"/> and opens it in a new browser tab.
    /// Never throws: every failure comes back as a ready-to-display message.
    /// </summary>
    public static async Task<EInvoicePortalOpenResult> OpenAsync(
        ISaEInvoiceService eInvoices,
        IJSRuntime jsRuntime,
        string? uuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return new(false, NoUuidMessage);
        }

        var (link, error) = await ResolveLinkAsync(eInvoices, uuid.Trim(), cancellationToken);
        if (error is not null)
        {
            return new(false, error.Message);
        }

        if (link is null)
        {
            return new(false, NoLinkMessage);
        }

        var (opened, message) = await OpenTabAsync(jsRuntime, link);
        return new(opened, message, link.Url);
    }

    /// <summary>
    /// The click behaviour that ALSO repairs the e-Invoice submission history:
    /// <see cref="ISaEInvoiceService.RepairSubmissionAsync"/> re-reads the document from MyInvois and
    /// create-or-updates its <c>dbo.EInvDocSubmission</c> row, so a row that was never written is created
    /// and a row that has drifted from LHDN is refreshed.
    ///
    /// <para>
    /// <b>Timing.</b> The normal path resolves the link, opens the tab, and only then repairs — so the
    /// click stays as fast as it is today and the repair can never delay or block the portal. When the
    /// link cannot be built at all (the row is missing, or the document is not viewable yet), the repair
    /// runs first and the link is retried once, because that is precisely the state the repair fixes.
    /// </para>
    ///
    /// <para>
    /// <b>Re-entry invariant: at most ONE repair and at most ONE link retry per click.</b> The retry
    /// re-resolves the link; it never repairs again. Any future change that loops "until the link
    /// appears" would turn a single click into an unbounded MyInvois call, so the fallback is a single
    /// pass by construction, not by luck.
    /// </para>
    ///
    /// <para>
    /// <b>Best-effort.</b> A failed repair is reported in the message but never changes
    /// <see cref="EInvoicePortalOpenResult.Opened"/>: opening the portal is the click's primary job.
    /// <see cref="EInvoicePortalOpenResult.StateChanged"/> tells the page whether it must reload its grid.
    /// </para>
    /// </summary>
    public static async Task<EInvoicePortalOpenResult> OpenAndRepairAsync(
        ISaEInvoiceService eInvoices,
        IJSRuntime jsRuntime,
        string? uuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return new(false, NoUuidMessage);
        }

        var wanted = uuid.Trim();
        var (link, error) = await ResolveLinkAsync(eInvoices, wanted, cancellationToken);

        // A resolve that THREW is a broken data layer, not a missing row: report it rather than spending a
        // MyInvois call on it.
        if (error is not null)
        {
            return new(false, error.Message);
        }

        if (link is null)
        {
            // Repair once, then re-resolve once (never a loop).
            var repaired = await TryRepairAsync(eInvoices, wanted, cancellationToken);
            (link, error) = await ResolveLinkAsync(eInvoices, wanted, cancellationToken);

            if (error is not null)
            {
                return new(false, error.Message, null, WroteSomething(repaired));
            }

            if (link is null)
            {
                return new(false, Compose(NoLinkMessage, repaired), null, WroteSomething(repaired));
            }

            var (retriedOpened, retriedMessage) = await OpenTabAsync(jsRuntime, link);
            return new(retriedOpened, Compose(retriedMessage, repaired), link.Url, WroteSomething(repaired));
        }

        // Normal path: the tab opens first and the repair happens behind it.
        var (opened, message) = await OpenTabAsync(jsRuntime, link);
        var repair = await TryRepairAsync(eInvoices, wanted, cancellationToken);

        return new(opened, Compose(message, repair), link.Url, WroteSomething(repair));
    }

    /// <summary>
    /// The standard E-UUID cell click for a list whose rows expose an e-Invoice status. It is the entry
    /// point every page should call; <see cref="OpenAndRepairAsync"/> is now the non-INVALID branch.
    ///
    /// <para>
    /// <b>Why INVALID goes somewhere else.</b> MyInvois only ever returns the validation failure reason
    /// through Get Document Details, and an INVALID document has no <c>longId</c> — so the portal link
    /// cannot even be built for it. The old behaviour could therefore only answer "No LHDN link for this
    /// document", which tells the operator nothing about what went wrong. The detail page is the one
    /// surface that can show the offending field, so an INVALID row navigates there instead.
    /// </para>
    ///
    /// <para>
    /// <b>Every other status is unchanged.</b> The click resolves the portal link, opens the tab and
    /// repairs the submission history, with the same "at most one repair, at most one link retry"
    /// invariant described on <see cref="OpenAndRepairAsync"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Zero MyInvois calls on the INVALID branch.</b> It navigates, so it never resolves a link and
    /// never repairs. That keeps the click inside LHDN's throttling guidance (Get Document Details is for
    /// reading invalid-document errors, not for status polling).
    /// </para>
    ///
    /// <para>Never throws: every outcome comes back as a ready-to-display message.</para>
    /// </summary>
    /// <param name="info">The clicked grid cell. Anything other than the UUID column is ignored.</param>
    /// <param name="irbmStatus">
    /// The row's <c>IRBMStatus</c>. The caller reads it from <c>info.Context</c>, which the grid sets to
    /// the row item; a null value simply means "not invalid".
    /// </param>
    /// <param name="hasDetailAccess">
    /// Whether the operator may open the detail page (the read-only LHDN tools ACCESS right). Without it
    /// an INVALID click opens nothing and says so, rather than bouncing the operator to /unauthorized or
    /// showing the useless "no link" message.
    /// </param>
    public static async Task<EInvoiceUuidClickResult> HandleUuidClickAsync(
        ISaEInvoiceService eInvoices,
        IJSRuntime jsRuntime,
        SelectedColumnInfo? info,
        string? irbmStatus,
        bool hasDetailAccess,
        CancellationToken cancellationToken = default)
    {
        if (!IsUuidColumn(info))
        {
            return EInvoiceUuidClickResult.Ignore();
        }

        if (EInvoiceStatuses.Normalize(irbmStatus) == EInvoiceStatuses.Invalid)
        {
            var (url, error) = EInvoiceDetailLink.Resolve(irbmStatus, info!.Value, hasDetailAccess);
            return url is not null
                ? EInvoiceUuidClickResult.NavigateTo(url)
                : EInvoiceUuidClickResult.Report(error!);
        }

        return EInvoiceUuidClickResult.FromPortal(
            await OpenAndRepairAsync(eInvoices, jsRuntime, info!.Value, cancellationToken));
    }

    /// <summary>
    /// Resolves the portal link, separating "no link" from "the lookup failed" so the caller can report
    /// each correctly. Never throws.
    /// </summary>
    private static async Task<(SaEInvoicePortalLink? Link, Exception? Error)> ResolveLinkAsync(
        ISaEInvoiceService eInvoices,
        string uuid,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await eInvoices.GetPortalLinkAsync(uuid, cancellationToken), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// Runs the history repair. Best-effort by contract: it swallows everything and returns null when the
    /// repair could not run, so the portal link is never affected by a repair failure.
    /// </summary>
    private static async Task<SaEInvoiceSubmissionRepairResult?> TryRepairAsync(
        ISaEInvoiceService eInvoices,
        string uuid,
        CancellationToken cancellationToken)
    {
        try
        {
            return await eInvoices.RepairSubmissionAsync(uuid, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<(bool Opened, string Message)> OpenTabAsync(
        IJSRuntime jsRuntime,
        SaEInvoicePortalLink link)
    {
        try
        {
            // The module is imported per click and released immediately: caching it in a static field
            // would share a circuit-bound JS handle between users.
            await using var module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
            var opened = await module.InvokeAsync<bool>("openInNewTab", link.Url);

            return opened
                ? (true, $"Opened this {link.Status} e-Invoice at the LHDN MyInvois portal.")
                : (false, $"The browser blocked the new tab. Open it manually: {link.Url}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>True when the repair actually wrote something, so the caller's grid is now stale.</summary>
    private static bool WroteSomething(SaEInvoiceSubmissionRepairResult? repair) =>
        repair is { Attempted: true }
        && (repair.Succeeded
            || repair.HistoryWrite is EInvoiceHistoryWriteResult.Inserted or EInvoiceHistoryWriteResult.Updated);

    /// <summary>The link outcome first, then whatever the repair had to say about the history.</summary>
    private static string Compose(string message, SaEInvoiceSubmissionRepairResult? repair) =>
        repair is null || string.IsNullOrWhiteSpace(repair.Message)
            ? message
            : message + " " + repair.Message;
}

/// <summary>
/// Outcome of one UUID-cell click: what to tell the operator, and the URL when one was resolved (so a
/// page may also offer it as a copyable link).
/// </summary>
/// <param name="Opened">
/// True when the portal tab opened. Says nothing about the history repair: a failed repair never fails
/// the click.
/// </param>
/// <param name="StateChanged">
/// True when the repair wrote something — the submission history row and/or the ERP document's e-Invoice
/// state. The page must reload its grid, because those columns are now stale.
/// </param>
public sealed record EInvoicePortalOpenResult(bool Opened, string Message, string? Url = null, bool StateChanged = false);

/// <summary>
/// Outcome of the standard E-UUID cell click. Exactly one of the three payloads is set, or none at all
/// when the clicked cell was not the e-Invoice UUID column.
/// </summary>
/// <param name="NavigateUrl">
/// Set when the click routes to the LHDN detail page (an INVALID document). The page navigates; nothing
/// was opened and no MyInvois call was made.
/// </param>
/// <param name="Message">
/// Set when the click was refused before anything happened, e.g. an INVALID document and the operator
/// lacks the LHDN tools ACCESS right.
/// </param>
/// <param name="Portal">
/// Set when the original portal-open-and-repair behaviour ran, so the page reports it exactly as before.
/// </param>
public sealed record EInvoiceUuidClickResult(
    string? NavigateUrl,
    string? Message,
    EInvoicePortalOpenResult? Portal)
{
    /// <summary>True when the clicked cell was not the e-Invoice UUID column, so nothing happened.</summary>
    public bool Ignored => NavigateUrl is null && Message is null && Portal is null;

    public static EInvoiceUuidClickResult Ignore() => new(null, null, null);

    public static EInvoiceUuidClickResult NavigateTo(string url) => new(url, null, null);

    public static EInvoiceUuidClickResult Report(string message) => new(null, message, null);

    public static EInvoiceUuidClickResult FromPortal(EInvoicePortalOpenResult portal) => new(null, null, portal);
}
