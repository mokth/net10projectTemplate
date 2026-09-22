using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// Read-only view of one MyInvois document, fetched live by UUID through
/// <see cref="ISaEInvoiceService.GetDocumentDetailAsync"/> — the "why is this INVALID?" screen.
///
/// <para>
/// It exists because an INVALID invoice / credit note / debit note shows only a status chip
/// elsewhere: the MyInvois <c>validationResults</c> that explain the failure are fetched and
/// discarded by the refresh path, so an operator has no way to see the offending field.
/// </para>
///
/// <para>
/// Nothing here mutates ERP data and nothing polls. The load happens once per navigation, and the
/// only way to re-read is the explicit Reload button — MyInvois throttles repeat calls for the same
/// document.
/// </para>
/// </summary>
public partial class SaEInvoiceDetail : Components.Pages.PageBase
{
    [Inject] private ISaEInvoiceService EInvoices { get; set; } = default!;

    /// <summary>MyInvois document UUID from the route (<c>/sales/einvoice/detail/{uuid}</c>).</summary>
    [Parameter] public string? Uuid { get; set; }

    /// <summary>
    /// Reuses the read-only LHDN inquiry menu rather than introducing one of its own, matching the
    /// <c>SA_EINVOICE_TIN</c> ACCESS check the service applies.
    /// </summary>
    protected string MenuCode => MenuCodes.SalesEInvoiceTin;

    protected SaEInvoiceDetailView? Detail { get; private set; }

    /// <summary>Page-local; <see cref="Components.Pages.PageBase.ErrorMessage"/> is not reused.</summary>
    protected string? StatusMessage { get; private set; }

    protected bool StatusIsError { get; private set; }

    protected override async Task OnPageInitializedAsync()
    {
        if (string.IsNullOrWhiteSpace(Uuid))
        {
            SetStatus("No MyInvois document UUID was supplied.", isError: true);
            return;
        }

        await LoadAsync();
    }

    protected async Task OnReloadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        ClearStatus();
        Detail = null;

        try
        {
            var result = await EInvoices.GetDocumentDetailAsync(Uuid!);
            if (result.Succeeded)
            {
                Detail = result.Detail;

                // A successful read with no detail payload would render an empty page; report it
                // rather than pretending the document has no validation errors.
                if (Detail is null)
                {
                    SetStatus("MyInvois returned no detail for this document.", isError: true);
                }
            }
            else
            {
                // The service owns the operator-facing wording for authorization, validation,
                // not-found and throttling; the page only decides that this is an error.
                SetStatus(result.ErrorMessage ?? "MyInvois could not return this document's detail.", isError: true);
            }
        }
        catch (Exception ex)
        {
            // GetDocumentDetailAsync is contractually non-throwing for expected failures; this is a
            // last-resort guard so the page can never render half-loaded.
            SetStatus($"Could not read this LHDN detail: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void DismissStatus() => ClearStatus();

    private void ClearStatus()
    {
        StatusMessage = null;
        StatusIsError = false;
    }

    private void SetStatus(string? message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    /// <summary>Document-level chip, using the same vocabulary as the e-Invoice status panel.</summary>
    protected string StatusCss => EInvoiceStatuses.Normalize(Detail?.Status).ToLowerInvariant() switch
    {
        "valid" => "ok",
        "submitted" or "submitting" => "pending",
        "invalid" or "rejected" or "failed" => "err",
        "cancelled" => "muted",
        _ => "new"
    };

    private static string StepStatusCss(string? status) =>
        (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "valid" => "ok",
            "invalid" => "err",
            "submitted" => "pending",
            _ => "new"
        };

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string Format(DateTime? value) =>
        value is null ? "—" : value.Value.ToString("dd/MM/yyyy HH:mm:ss");

    private static string Format(decimal? value) =>
        value is null ? "—" : value.Value.ToString("N2");
}
