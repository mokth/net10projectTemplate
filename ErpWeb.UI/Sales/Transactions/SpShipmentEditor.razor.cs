using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SpShipmentEditor
{
    [Parameter] public string InvNo { get; set; } = string.Empty;
    [Parameter] public int SoLineNo { get; set; }
    [Parameter] public byte[]? RowVersion { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public EventCallback OnCancelled { get; set; }
    [Parameter] public EventCallback<SaInvoiceDocument> OnApplied { get; set; }
    [Parameter] public EventCallback<(string Message, SaInvoiceDocument? Document)> OnApplyFailed { get; set; }

    [Inject] private ISaInvoiceService Invoices { get; set; } = default!;

    protected string? ErrorMessage;
    protected bool IsSubmitting;
    private int _soLineNo;
    private decimal _requestedStdQty;
    private readonly List<EditorRow> _rows = [];

    protected override async Task OnParametersSetAsync()
    {
        if (SoLineNo == _soLineNo && _rows.Count > 0)
        {
            return;
        }

        _soLineNo = SoLineNo;
        await LoadSnapshotAsync();
    }

    private async Task LoadSnapshotAsync()
    {
        ErrorMessage = null;
        _rows.Clear();
        var result = await Invoices.GetShipmentEditAsync(InvNo, SoLineNo);
        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load shipment edit snapshot.";
            return;
        }

        var line = result.Document.Lines.FirstOrDefault(x => x.Line == SoLineNo);
        _requestedStdQty = line?.StdQty ?? 0m;
        foreach (var lot in result.Document.Shipment.Where(x => x.Line == SoLineNo))
        {
            _rows.Add(new EditorRow
            {
                Key = lot.FromBalLocId ?? 0,
                FromBalLocId = lot.FromBalLocId ?? 0,
                FrLotNo = lot.FrLotNo,
                FrLocation = lot.FrLocation,
                Available = lot.CurrentAvailableQty,
                IssueQty = lot.FrStdQty,
                FailReason = lot.FailReason
            });
        }
    }

    private void OnIssueChanged(EditorRow row, decimal value)
    {
        // Do not wipe unsaved input on failure; only local edit while typing.
        row.IssueQty = value;
    }

    private async Task OnApplyAsync()
    {
        if (IsSubmitting || !Enabled)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var lots = _rows
                .Where(x => x.FromBalLocId > 0 && x.IssueQty > 0m)
                .Select(x => new SaInvoiceShipmentLotRequest
                {
                    FromBalLocId = x.FromBalLocId,
                    IssueQty = x.IssueQty
                })
                .ToList();

            var result = await Invoices.ReplaceShipmentLineAsync(InvNo, SoLineNo, lots, RowVersion);
            if (result.Succeeded && result.Document is not null)
            {
                await OnApplied.InvokeAsync(result.Document);
                return;
            }

            // Preserve unsaved input; merge fail reasons / available from server.
            if (result.Document?.Shipment is { Count: > 0 })
            {
                foreach (var fail in result.Document.Shipment.Where(x => x.Line == SoLineNo))
                {
                    var row = _rows.FirstOrDefault(x => x.FromBalLocId == fail.FromBalLocId);
                    if (row is null)
                    {
                        continue;
                    }

                    row.Available = fail.CurrentAvailableQty;
                    row.FailReason = fail.FailReason;
                    // Keep row.IssueQty as submitted — do not overwrite with server FrStdQty.
                }
            }

            ErrorMessage = result.ErrorMessage ?? "Shipment apply failed.";
            await OnApplyFailed.InvokeAsync((ErrorMessage, result.Document));
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private Task OnCancel() => OnCancelled.InvokeAsync();

    private sealed class EditorRow
    {
        public int Key { get; set; }
        public int FromBalLocId { get; set; }
        public string? FrLotNo { get; set; }
        public string? FrLocation { get; set; }
        public decimal? Available { get; set; }
        public decimal IssueQty { get; set; }
        public string? FailReason { get; set; }
    }
}
