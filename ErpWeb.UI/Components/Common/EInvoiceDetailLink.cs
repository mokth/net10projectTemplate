using ErpWeb.Core.EInvoice;

namespace ErpWeb.UI.Components.Common;

/// <summary>
/// The route and guard rules for the LHDN document detail screen, shared by every grid that offers it
/// (sales invoice and credit/debit note today).
///
/// <para>
/// Mirrors <see cref="EInvoicePortalLinkOpener"/>: the URL shape and the "may this be opened at all?"
/// rule live in exactly one place, so the desktop row action and the mobile compact link cannot drift
/// apart, and a future self-billed list reuses them verbatim.
/// </para>
/// </summary>
public static class EInvoiceDetailLink
{
    /// <summary>Route prefix; the page is <c>/sales/einvoice/detail/{uuid}</c>.</summary>
    public const string Route = "/sales/einvoice/detail";

    private const string NoUuidMessage = "This document has no MyInvois UUID.";

    private const string NoAccessMessage =
        "You do not have access to the LHDN e-Invoice tools, so this detail cannot be opened.";

    /// <summary>
    /// Shown when the action is attempted on a row that is not INVALID.
    ///
    /// <para>
    /// This gate is <b>intentional</b>, not a limitation: MyInvois directs that Get Document Details
    /// be used only to retrieve error details for invalid documents, and repeat requests for the same
    /// document risk throttling. The page itself renders any status; only the entry point is gated.
    /// </para>
    /// </summary>
    public const string InvalidOnlyMessage =
        "LHDN detail is available only while the e-Invoice status is INVALID.";

    /// <summary>
    /// Resolves the detail URL for one grid row, or the reason it cannot be opened. Returning the
    /// reason keeps every caller's message identical.
    /// </summary>
    /// <param name="irbmStatus">The row's <c>IRBMStatus</c>.</param>
    /// <param name="irbmUuid">The row's <c>IRBMUUID</c>.</param>
    /// <param name="hasAccess">Whether the caller holds the LHDN tools (ACCESS) right.</param>
    public static (string? Url, string? Error) Resolve(
        string? irbmStatus,
        string? irbmUuid,
        bool hasAccess)
    {
        if (!hasAccess)
        {
            return (null, NoAccessMessage);
        }

        if (string.IsNullOrWhiteSpace(irbmUuid))
        {
            return (null, NoUuidMessage);
        }

        if (EInvoiceStatuses.Normalize(irbmStatus) != EInvoiceStatuses.Invalid)
        {
            return (null, InvalidOnlyMessage);
        }

        return ($"{Route}/{Uri.EscapeDataString(irbmUuid.Trim())}", null);
    }
}
