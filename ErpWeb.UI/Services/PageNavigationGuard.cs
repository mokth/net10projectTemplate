namespace ErpWeb.UI.Services;

public sealed class PageNavigationGuard
{
    private readonly Dictionary<int, string> _scopes = new();
    private readonly List<int> _order = new();
    private int _nextId;

    public bool IsBlocking => _scopes.Count > 0;

    public string? Message =>
        _order.Count == 0 ? null : _scopes[_order[^1]];

    public event Action? Changed;

    public IDisposable Begin(string message)
    {
        var id = _nextId++;
        _scopes[id] = message;
        _order.Add(id);
        NotifyChanged();
        return new BlockingScope(this, id);
    }

    private void End(int scopeId)
    {
        if (!_scopes.Remove(scopeId))
        {
            return;
        }

        _order.Remove(scopeId);
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    private sealed class BlockingScope : IDisposable
    {
        private PageNavigationGuard? _owner;
        private readonly int _scopeId;
        private bool _disposed;

        public BlockingScope(PageNavigationGuard owner, int scopeId)
        {
            _owner = owner;
            _scopeId = scopeId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner?.End(_scopeId);
            _owner = null;
        }
    }
}
