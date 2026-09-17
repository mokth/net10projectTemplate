using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// LHDN e-Invoice TIN tools — read-only inquiries against the MyInvois taxpayer API:
/// validate a TIN against an identity document, and search taxpayers by name / identity document.
/// <para>
/// Like every other MyInvois surface this goes through <see cref="ISaEInvoiceService"/>; nothing here
/// touches the e-Invoice repository directly. Nothing on this screen mutates ERP data.
/// </para>
/// </summary>
public partial class SaEInvoiceTin : Components.Pages.PageBase
{
    [Inject] private ISaEInvoiceService EInvoices { get; set; } = default!;

    protected string MenuCode => MenuCodes.SalesEInvoiceTin;

    protected static IReadOnlyList<string> IdTypes { get; } = ["NRIC", "BRN", "PASSPORT", "ARMY"];

    // ── Validate ──
    protected string ValidateIdType { get; set; } = "BRN";
    protected string? ValidateIdValue { get; set; }
    protected string? ValidateTinValue { get; set; }
    protected SaEInvoiceTinCheckResult? ValidateResult { get; set; }

    // ── Search ──
    protected string? SearchName { get; set; }
    protected string? SearchIdType { get; set; }
    protected string? SearchIdValue { get; set; }
    protected IReadOnlyList<SaEInvoiceTinSearchRow> SearchRows { get; set; } = [];
    protected bool HasSearched { get; set; }
    protected string? SearchError { get; set; }

    /// <summary>Local to this page; <see cref="Components.Pages.PageBase.ErrorMessage"/> is not reused.</summary>
    protected string? StatusMessage { get; set; }
    protected bool StatusIsError { get; set; }

    private CancellationTokenSource _cts = new();

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected async Task OnValidateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ValidateResult = null;
        ClearStatus();

        try
        {
            ValidateResult = await EInvoices.ValidateTinAsync(
                ValidateIdType,
                ValidateIdValue ?? string.Empty,
                ValidateTinValue ?? string.Empty,
                _cts.Token);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected async Task OnSearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SearchRows = [];
        HasSearched = false;
        SearchError = null;
        ClearStatus();

        try
        {
            var result = await EInvoices.SearchTinAsync(new SaEInvoiceTinSearchQuery
            {
                TaxpayerName = SearchName,
                IdType = SearchIdType,
                IdValue = SearchIdValue
            }, _cts.Token);

            HasSearched = true;
            if (result.Succeeded)
            {
                SearchRows = result.Rows;
                SetStatus(result.Rows.Count == 1 ? "1 TIN found." : $"{result.Rows.Count} TINs found.", isError: false);
            }
            else
            {
                SearchError = result.ErrorMessage;
            }
        }
        catch (Exception ex)
        {
            HasSearched = true;
            SearchError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void ClearStatus()
    {
        StatusMessage = null;
        StatusIsError = false;
    }

    protected void DismissStatus() => ClearStatus();

    private void SetStatus(string? message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
