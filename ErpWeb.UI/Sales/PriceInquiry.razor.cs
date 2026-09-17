using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales;

/// <summary>
/// Phase 6 — the price-inquiry screen. Read-only: it shows the final price AND the ladder that produced
/// it, so an operator can answer "why didn't the customer's special price apply?" without reading code.
///
/// It calls <see cref="ISaSalesRefService.ExplainLinePriceAsync"/>, which walks the SAME sources through
/// the SAME helpers as the resolver, so this screen can never disagree with a document.
///
/// "No price" is a legitimate answer, not an error: the ladder then explains every level that was tried.
/// </summary>
public partial class PriceInquiry : ComponentBase
{
    [Inject] private ISaSalesRefService SalesRef { get; set; } = default!;
    [Inject] private ISaCustLookupService CustLookups { get; set; } = default!;
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;

    protected string? CustCode { get; set; }
    protected string? CustomerSearch { get; set; }
    protected string? ICode { get; set; }
    protected string? Uom { get; set; }
    protected decimal? Qty { get; set; } = 1m;
    protected DateTime? DocDate { get; set; }
    protected string? Currency { get; set; }
    protected decimal? TaxPercent { get; set; }
    protected bool IsInclusive { get; set; }

    protected bool IsBusy { get; set; }
    protected string? ErrorMessage { get; set; }
    protected SaPriceExplanation? Result { get; set; }
    protected decimal? InclusivePrice { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> UomOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CurrencyOptions { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        // The date defaults to the company's business date so the validity window is judged against the
        // date a document would actually use.
        DocDate ??= Dates.Now.Date;

        var uoms = await InventoryLookups.ListActiveUomsAsync();
        UomOptions = uoms.Succeeded ? uoms.Rows : [];

        CurrencyOptions = await CustLookups.ListCurrenciesForAssignmentAsync();
        CustomerOptions = await CustLookups.SearchCustomersAsync(string.Empty, 200);
    }

    /// <summary>
    /// Typos are the main reason an inquiry returns nothing, so the customer list is refreshed as the
    /// operator types rather than requiring an exact code up front.
    /// </summary>
    protected async Task OnCustomerSearchChangedAsync(string? value)
    {
        CustomerSearch = value;
        CustomerOptions = await CustLookups.SearchCustomersAsync(value ?? string.Empty, 200);
    }

    protected void OnItemSelected(IvStockMasterLookupRow item)
    {
        ICode = item.ICode;
        if (string.IsNullOrWhiteSpace(Uom))
        {
            Uom = item.StdUom;
        }
    }

    protected string Headline => Result is null
        ? string.Empty
        : Result.Found
            ? "A price was found"
            : "No price found";

    /// <summary>Renders a level's band / window / currency, or a dash when the level carries none.</summary>
    protected static string Basis(SaPriceExplanationLevel level)
    {
        var parts = new List<string>();

        if (level.MinQty is not null || level.MaxQty is not null)
        {
            // Read both through the property rather than pattern variables: with `||` the compiler cannot
            // prove either pattern variable was assigned on every path.
            var min = level.MinQty ?? 0m;
            var ceiling = level.MaxQty is null ? "and above" : $"to {level.MaxQty:0.####}";
            parts.Add($"qty {min:0.####} {ceiling}");
        }

        if (level.ValidFrom is { } from || level.ValidTo is { } to)
        {
            var start = level.ValidFrom?.ToString("yyyy-MM-dd") ?? "always";
            var end = level.ValidTo?.ToString("yyyy-MM-dd") ?? "open";
            parts.Add($"{start} → {end}");
        }

        if (!string.IsNullOrWhiteSpace(level.Ref))
        {
            parts.Add(level.Ref!);
        }

        if (!string.IsNullOrWhiteSpace(level.Currency))
        {
            parts.Add(level.Currency!);
        }

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    protected async Task ExplainAsync()
    {
        if (string.IsNullOrWhiteSpace(CustCode) || string.IsNullOrWhiteSpace(ICode) || string.IsNullOrWhiteSpace(Uom))
        {
            ErrorMessage = "Customer, item and UOM are required.";
            Result = null;
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var response = await SalesRef.ExplainLinePriceAsync(new SaLinePricingRequest
            {
                CustCode = CustCode,
                ICode = ICode,
                UOM = Uom,
                Qty = Qty ?? 1m,
                DocDate = DocDate ?? Dates.Now.Date,
                DocumentCurrency = Currency,
                TaxPercent = TaxPercent ?? 0m,
                IsInclusive = IsInclusive
            });

            if (!response.Succeeded)
            {
                // A missing customer is a genuine error; a missing PRICE is not - that comes back as a
                // successful explanation whose ladder shows every level that was tried.
                ErrorMessage = response.Message ?? "Unable to explain a price.";
                Result = null;
                InclusivePrice = null;
                return;
            }

            Result = response.Data;
            InclusivePrice = Result is { Found: true, UnitPrice: { } exclusive } && IsInclusive
                ? SaItemFamilyPriceBasis.ToInclusive(exclusive, (TaxPercent ?? 0m) / 100m)
                : null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
