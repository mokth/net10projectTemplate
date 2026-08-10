namespace ErpWeb.Core.Menus;

/// <summary>
/// Process-wide menu tree cache. Invalidated after successful XML sync.
/// </summary>
public sealed class MenuCache
{
    private readonly object _gate = new();
    private IReadOnlyList<MenuNavItem>? _tree;

    public IReadOnlyList<MenuNavItem>? Get()
    {
        lock (_gate)
        {
            return _tree;
        }
    }

    public void Set(IReadOnlyList<MenuNavItem> tree)
    {
        lock (_gate)
        {
            _tree = tree;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _tree = null;
        }
    }
}
