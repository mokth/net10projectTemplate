using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// E3 — shared read-only document-flow panel. Shows which documents fed the current one, and which
/// documents it fed, from the allocation ledger plus the CN references. Diagnosis only: it never
/// mutates and its failure is silent (an empty panel), because flow is supporting information.
/// </summary>
public partial class SaDocFlowPanel : ComponentBase
{
    [Inject] private ISaDocFlowQuery Flow { get; set; } = default!;

    [Parameter] public string? DocType { get; set; }
    [Parameter] public string? DocNo { get; set; }

    private SaDocFlowResult? _result;
    private string _loadedKey = string.Empty;

    protected override async Task OnParametersSetAsync()
    {
        var type = (DocType ?? string.Empty).Trim();
        var no = (DocNo ?? string.Empty).Trim();
        var key = $"{type}:{no}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        _result = null;

        // Unsaved / new documents have no identity yet — do not query.
        if (type.Length == 0 || no.Length == 0)
        {
            return;
        }

        try
        {
            _result = await Flow.QueryAsync(type, no);
        }
        catch (Exception)
        {
            // Supporting information only — never break the document screen because flow failed.
            _result = null;
        }
    }
}
