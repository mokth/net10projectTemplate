namespace ErpWeb.Core.Authentication;

/// <summary>
/// Authorization policy names shared by the web host, the Blazor page bases and the minimal-API
/// endpoints, so the forced-password-change gate has exactly one spelling.
/// </summary>
public static class ErpAuthPolicies
{
    /// <summary>
    /// Authenticated AND not required to change the password. Normal ERP pages and every protected
    /// non-component endpoint (exports, menu administration, attachments) use this policy so a
    /// pending forced change cannot be bypassed by calling an endpoint directly instead of a page.
    /// </summary>
    public const string ErpAccess = "ErpAccess";
}
