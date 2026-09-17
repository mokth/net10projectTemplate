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

    /// <summary>
    /// Deterministic order for a server validation dictionary: document-level keys first
    /// (alphabetical), then line keys by row. Every surface (headline, panel, grid column) uses this
    /// so they can never disagree. See <see cref="ValidationMessageFormat"/> for the full contract.
    /// </summary>
    protected static IReadOnlyList<KeyValuePair<string, string>> OrderValidationErrors(
        IReadOnlyDictionary<string, string>? errors) =>
        ValidationMessageFormat.Order(errors);

    /// <summary>
    /// Operator-facing headline for a validation failure. Composes the concrete causes; the generic
    /// "Validation failed." is only ever returned when the dictionary holds nothing usable.
    /// This is a convenience summary — the dictionary stays authoritative.
    /// </summary>
    protected static string BuildValidationMessage(
        IReadOnlyDictionary<string, string>? errors,
        string? serverMessage) =>
        ValidationMessageFormat.BuildHeadline(errors, serverMessage);

    /// <summary>Every message for one grid row (1-based), never collapsed to the first.</summary>
    protected static IReadOnlyList<string> LineErrorMessages(
        IReadOnlyDictionary<string, string>? errors,
        int lineNo) =>
        ValidationMessageFormat.LineMessages(errors, lineNo);

    /// <summary>Friendly label for a validation key; unknown keys are returned verbatim.</summary>
    protected static string ValidationFieldLabel(string? key) =>
        ValidationMessageFormat.Label(key);
}
