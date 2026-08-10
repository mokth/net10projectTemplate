using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Menus;

public sealed class MenuService : IMenuService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MenuCache _cache;
    private readonly ILogger<MenuService> _logger;

    public MenuService(
        IDbContextFactory<AppDbContext> dbFactory,
        MenuCache cache,
        ILogger<MenuService> logger)
    {
        _dbFactory = dbFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MenuNavItem>> GetActiveTreeAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cache.Get();
        if (cached is not null)
        {
            return cached;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var menus = await db.Menus
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.MenuCode)
            .ToListAsync(cancellationToken);

        var byParent = menus
            .GroupBy(m => m.ParentMenuId)
            .ToDictionary(g => g.Key ?? -1, g => g.ToList());

        IReadOnlyList<MenuNavItem> Build(int? parentId)
        {
            if (!byParent.TryGetValue(parentId ?? -1, out var children))
            {
                return Array.Empty<MenuNavItem>();
            }

            return children
                .Select(m => new MenuNavItem
                {
                    Code = m.MenuCode,
                    Name = m.MenuName,
                    Route = m.Route,
                    Icon = m.Icon,
                    SortOrder = m.SortOrder,
                    AlwaysVisible = m.AlwaysVisible,
                    Children = Build(m.MenuId)
                })
                .ToList();
        }

        var tree = Build(null);
        _cache.Set(tree);
        _logger.LogDebug("Loaded {Count} active menus into menu cache", menus.Count);
        return tree;
    }

    public void InvalidateCache() => _cache.Invalidate();
}
