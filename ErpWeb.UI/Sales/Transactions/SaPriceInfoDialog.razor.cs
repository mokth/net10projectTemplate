using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaPriceInfoDialog : ComponentBase, IDisposable
{
    [Inject] private ISaSalesRefService SalesRefService { get; set; } = default!;

    [Parameter] public SaPriceInfoContext? Context { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    private SaPriceInfoContext? _observedContext;
    private SaPriceExplanation? _explanation;
    private CancellationTokenSource? _checkCancellation;
    private string? _errorMessage;
    private bool _isChecking;
    private int _checkGeneration;

    private bool CanCheckCurrentRules => Context is not null &&
        !string.IsNullOrWhiteSpace(Context.CustCode) &&
        !string.IsNullOrWhiteSpace(Context.ItemCode) &&
        !string.IsNullOrWhiteSpace(Context.Uom);

    private string SourceDocumentDescription
    {
        get
        {
            if (Context is null)
                return string.Empty;

            var line = Context.SourceDocumentLine is > 0 ? $" · Line {Context.SourceDocumentLine}" : string.Empty;
            return $"{Context.SourceDocumentType ?? "Source document"} {Context.SourceDocumentNo}{line}";
        }
    }

    private string ComparisonHeading
    {
        get
        {
            if (Context is null || _explanation is null)
                return string.Empty;

            var currentPrice = CurrentPrice;
            var sameSource = _explanation.WinningSource is { } winning &&
                string.Equals(SaPriceSourceTokens.For(winning), Context.PricingSource, StringComparison.OrdinalIgnoreCase);
            var samePrice = currentPrice is { } price && price == Context.UnitPrice;
            return _explanation.Found && sameSource && samePrice
                ? "Current rules still resolve to the recorded source and price."
                : "Current pricing rules now resolve differently.";
        }
    }

    private decimal? CurrentPrice => _explanation is { Found: true, UnitPrice: { } price } && Context is not null
        ? ToDocumentBasis(price, Context)
        : null;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(Context, _observedContext))
            return;

        _observedContext = Context;
        _checkGeneration++;
        _checkCancellation?.Cancel();
        _checkCancellation = null;
        _isChecking = false;
        _errorMessage = null;
        _explanation = null;
    }

    private async Task CheckCurrentPricingRulesAsync()
    {
        var context = Context;
        if (!CanCheckCurrentRules || context is null || _isChecking)
            return;

        var generation = ++_checkGeneration;
        var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        _isChecking = true;
        _errorMessage = null;
        _explanation = null;

        try
        {
            var result = await SalesRefService.ExplainLinePriceAsync(new SaLinePricingRequest
            {
                CustCode = context.CustCode,
                ICode = context.ItemCode,
                UOM = context.Uom,
                Qty = context.Qty,
                DocDate = context.DocDate,
                DocumentCurrency = context.Currency,
                TaxPercent = context.TaxPercent,
                IsInclusive = context.IsInclusive
            }, cancellation.Token);

            if (generation != _checkGeneration || !ReferenceEquals(Context, context))
                return;

            if (!result.Succeeded || result.Data is null)
            {
                _errorMessage = "Unable to check current pricing rules.";
                return;
            }

            _explanation = result.Data;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The context changed while the optional request was running.
        }
        catch
        {
            if (generation == _checkGeneration && ReferenceEquals(Context, context))
                _errorMessage = "Unable to check current pricing rules.";
        }
        finally
        {
            if (generation == _checkGeneration)
            {
                _isChecking = false;
                _checkCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private Task OnVisibleChangedAsync(bool visible) => VisibleChanged.InvokeAsync(visible);

    private Task CloseAsync() => VisibleChanged.InvokeAsync(false);

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private string FormatCurrentPrice() => CurrentPrice is { } price && Context is not null
        ? SaPriceInfoPresentation.FormatPrice(price, Context.Currency)
        : "No usable price";

    private string FormatLevelPrice(SaPriceExplanationLevel level)
    {
        if (level.UnitPrice is not { } price || Context is null)
            return "—";

        return SaPriceInfoPresentation.FormatPrice(ToDocumentBasis(price, Context), Context.Currency);
    }

    private static decimal ToDocumentBasis(decimal taxExclusivePrice, SaPriceInfoContext context) =>
        context.IsInclusive
            ? SaItemFamilyPriceBasis.ToInclusive(taxExclusivePrice, context.TaxPercent / 100m)
            : taxExclusivePrice;

    private static string LevelResult(SaPriceExplanationLevel level) =>
        !level.Eligible ? "Excluded" : level.Applied ? "Used" : "Not used";

    public void Dispose()
    {
        _checkCancellation?.Cancel();
        _checkCancellation = null;
    }
}
