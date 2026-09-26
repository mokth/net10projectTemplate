// LHDN MyInvois portal helper.
//
// Same shape as js/po-attach.js: an ES module imported on demand from C# with
//   await using var module = await js.InvokeAsync<IJSObjectReference>("import", "/js/einvoice-portal.js");
// so nothing has to be registered on window or loaded with a <script> tag.

/**
 * Opens an absolute external URL (the MyInvois portal share page) in a new browser tab.
 *
 * Returns true only when the browser actually handed us a new window, so the caller can tell the
 * operator when a pop-up blocker swallowed it.
 *
 * Note: window.open(url, "_blank", "noopener") is deliberately NOT used - Chrome returns null in that
 * case even on success, which would look like a blocked pop-up. We open without features and then
 * sever window.opener, which gives the same protection without hiding the result.
 */
export function openInNewTab(url) {
    if (!url) {
        return false;
    }

    const opened = window.open(url, "_blank");
    if (!opened) {
        return false;
    }

    try {
        opened.opener = null;
    } catch {
        // Cross-origin windows may refuse this; the tab is already open, so it is not an error.
    }

    return true;
}
