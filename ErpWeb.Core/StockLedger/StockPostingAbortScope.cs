namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Keeps a caller-owned SQL session write marker scoped to its unsealed root.
/// Dispose runs before the caller's catch/transaction disposal on failed paths.
/// </summary>
internal sealed class StockPostingAbortScope : IAsyncDisposable
{
    private readonly IStockPostingCoordinator? _coordinator;
    private readonly StockPostingContext? _context;
    private bool _completed;

    public StockPostingAbortScope(IStockPostingCoordinator? coordinator, StockPostingContext? context)
    {
        _coordinator = coordinator;
        _context = context;
    }

    public void MarkCompleted() => _completed = true;

    public async ValueTask DisposeAsync()
    {
        if (!_completed && _coordinator is not null && _context is not null)
            await _coordinator.AbortInTransactionAsync(_context, CancellationToken.None);
    }
}
