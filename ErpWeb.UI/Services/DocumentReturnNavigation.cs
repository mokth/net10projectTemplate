using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Services;

/// <summary>
/// Safe return-path handling for document pages opened from an Inquiry grid
/// (Purchase Inquiry, Sales Inquiry) or the Costing Diagnostic &amp; Repair Center.
/// Only same-app paths under a supported inquiry group are accepted — never open redirects.
/// </summary>
public static class DocumentReturnNavigation
{
    public const string QueryKey = "returnUrl";

    /// <summary>
    /// The only root-absolute path prefixes a <c>returnUrl</c> may point at. Deliberately an
    /// allow-list: adding a group here is what grants its documents the Close-returns-to-inquiry
    /// behaviour, so a free-form path can never become an open redirect.
    /// </summary>
    private static readonly string[] AllowedInquiryPrefixes =
    [
        "/purchase/inquiry/",
        "/sales/inquiry/",
        "/inventory/costing-center"
    ];

    /// <summary>Appends a returnUrl query parameter (replaces an existing one).</summary>
    public static string WithReturnUrl(string documentUrl, string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(documentUrl) || !IsSafeInquiryReturn(returnUrl))
        {
            return documentUrl;
        }

        var parts = documentUrl.Split('?', 2);
        var path = parts[0];
        var query = parts.Length > 1
            ? QueryHelpers.ParseQuery(parts[1])
                .ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        query[QueryKey] = returnUrl!.Trim();
        return QueryHelpers.AddQueryString(path, query);
    }

    public static bool TryGetSafeInquiryReturn(string? absoluteOrRelativeUri, out string returnUrl)
    {
        returnUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(absoluteOrRelativeUri))
        {
            return false;
        }

        string? raw = null;
        if (Uri.TryCreate(absoluteOrRelativeUri, UriKind.Absolute, out var abs))
        {
            raw = QueryHelpers.ParseQuery(abs.Query).TryGetValue(QueryKey, out var values)
                ? values.FirstOrDefault()
                : null;
        }
        else
        {
            var qIndex = absoluteOrRelativeUri.IndexOf('?', StringComparison.Ordinal);
            if (qIndex >= 0)
            {
                raw = QueryHelpers.ParseQuery(absoluteOrRelativeUri[qIndex..])
                    .TryGetValue(QueryKey, out var values)
                    ? values.FirstOrDefault()
                    : null;
            }
        }

        if (!IsSafeInquiryReturn(raw))
        {
            return false;
        }

        returnUrl = raw!.Trim();
        return true;
    }

    /// <summary>
    /// True when the current location carries a safe inquiry returnUrl — the sidebar must not
    /// Prefix-match the transaction document route (which would expand Transactions and steal focus).
    /// </summary>
    public static bool HasSafeInquiryReturn(string? absoluteUri) =>
        TryGetSafeInquiryReturn(absoluteUri, out _);

    public static bool IsSafeInquiryReturn(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return false;
        }

        var value = returnUrl.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains('\r')
            || value.Contains('\n'))
        {
            return false;
        }

        // Root-absolute app path under a supported inquiry group only.
        if (!AllowedInquiryPrefixes.Any(
                prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Reject path tricks.
        if (value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>Copies a safe inquiry returnUrl from the current location onto a new document URL.</summary>
    public static string PreserveReturnUrl(string? currentAbsoluteUri, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return targetUrl ?? string.Empty;
        }

        return TryGetSafeInquiryReturn(currentAbsoluteUri, out var returnUrl)
            ? WithReturnUrl(targetUrl, returnUrl)
            : targetUrl;
    }

    public static void NavigateBack(AppNavigation navigation, string fallbackListRoute)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (TryGetSafeInquiryReturn(navigation.Uri, out var returnUrl))
        {
            navigation.NavigateTo(returnUrl);
            return;
        }

        navigation.NavigateTo(fallbackListRoute);
    }
}
