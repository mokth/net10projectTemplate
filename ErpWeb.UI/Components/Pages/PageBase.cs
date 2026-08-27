using ErpWeb.Core.Services;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

[Authorize]
public abstract class PageBase : ComponentBase
{
    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    [Inject]
    protected ICurrentUserService CurrentUser { get; set; } = default!;

    [Inject]
    protected PageNavigationGuard NavigationGuard { get; set; } = default!;

    protected bool IsBusy { get; set; }

    protected string? ErrorMessage { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await OnPageInitializedAsync();
    }

    protected virtual Task OnPageInitializedAsync() => Task.CompletedTask;

    protected IDisposable BeginBlockingWork(string message) =>
        NavigationGuard.Begin(message);
}
