using ErpWeb.Core.Services;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

/// <summary>
/// Base for pages that require an authenticated user but are explicitly exempt from the
/// forced-password-change gate (ErpAccess). The Change Password page is the only normal consumer:
/// a user who must change their password still has to reach it. It is NOT anonymous.
/// </summary>
[Authorize]
public abstract class AuthenticatedPageBase : ComponentBase
{
    [Inject]
    protected AppNavigation Navigation { get; set; } = default!;

    [Inject]
    protected ICurrentUserService CurrentUser { get; set; } = default!;

    protected string? ErrorMessage { get; set; }
}
