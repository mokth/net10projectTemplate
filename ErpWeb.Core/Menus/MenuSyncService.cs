using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Menus;

public sealed class MenuSyncService : IMenuSyncService
{
    private readonly IMenuDefinitionService _definitions;
    private readonly IMenuService _menuService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<MenuSyncService> _logger;

    public MenuSyncService(
        IMenuDefinitionService definitions,
        IMenuService menuService,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<MenuSyncService> logger)
    {
        _definitions = definitions;
        _menuService = menuService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public Task<MenuSyncResult> PreviewXmlSyncAsync(CancellationToken cancellationToken = default) =>
        SyncCoreAsync(apply: false, cancellationToken);

    public Task<MenuSyncResult> SyncFromXmlAsync(CancellationToken cancellationToken = default) =>
        SyncCoreAsync(apply: true, cancellationToken);

    private async Task<MenuSyncResult> SyncCoreAsync(bool apply, CancellationToken cancellationToken)
    {
        var validationErrors = _definitions.Validate();
        if (validationErrors.Count > 0)
        {
            return MenuSyncResult.Failed(validationErrors);
        }

        var flat = _definitions.GetFlatByCode();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        if (apply)
        {
            var accessExists = await db.Permissions
                .AsNoTracking()
                .AnyAsync(p => p.PermissionCode == PermissionCodes.Access, cancellationToken);
            if (!accessExists)
            {
                return MenuSyncResult.Failed(["ACCESS permission is missing from the database."]);
            }
        }

        await using var tx = apply
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var existing = await db.Menus.ToListAsync(cancellationToken);
            var byCode = existing.ToDictionary(m => m.MenuCode, StringComparer.OrdinalIgnoreCase);

            var inserted = new List<string>();
            var updated = new List<string>();
            var unchanged = new List<string>();
            var disabled = new List<string>();

            // Pass 1: upsert metadata (parents resolved in pass 2)
            foreach (var def in flat.Values)
            {
                if (byCode.TryGetValue(def.Code, out var menu))
                {
                    var changed =
                        !string.Equals(menu.MenuName, def.Name, StringComparison.Ordinal) ||
                        !string.Equals(menu.Route, def.Route, StringComparison.Ordinal) ||
                        !string.Equals(menu.Icon, def.Icon, StringComparison.Ordinal) ||
                        menu.SortOrder != def.SortOrder ||
                        menu.AlwaysVisible != def.AlwaysVisible ||
                        !menu.IsActive;

                    if (changed)
                    {
                        if (apply)
                        {
                            menu.MenuName = def.Name;
                            menu.Route = def.Route;
                            menu.Icon = def.Icon;
                            menu.SortOrder = def.SortOrder;
                            menu.AlwaysVisible = def.AlwaysVisible;
                            menu.IsActive = true;
                            menu.ModifiedDate = DateTime.UtcNow;
                            menu.ModifiedBy = "XMLSYNC";
                        }

                        updated.Add(def.Code);
                    }
                    else
                    {
                        unchanged.Add(def.Code);
                    }
                }
                else
                {
                    if (apply)
                    {
                        var created = new Menu
                        {
                            MenuCode = def.Code,
                            MenuName = def.Name,
                            Route = def.Route,
                            Icon = def.Icon,
                            SortOrder = def.SortOrder,
                            AlwaysVisible = def.AlwaysVisible,
                            IsActive = true,
                            CreatedDate = DateTime.UtcNow,
                            CreatedBy = "XMLSYNC"
                        };
                        db.Menus.Add(created);
                        byCode[def.Code] = created;
                    }

                    inserted.Add(def.Code);
                }
            }

            if (!apply)
            {
                foreach (var menu in existing)
                {
                    if (!flat.ContainsKey(menu.MenuCode) && menu.IsActive)
                    {
                        disabled.Add(menu.MenuCode);
                    }
                }

                foreach (var def in flat.Values)
                {
                    if (!byCode.TryGetValue(def.Code, out var menu))
                    {
                        continue;
                    }

                    int? parentId = null;
                    if (def.ParentCode is not null && byCode.TryGetValue(def.ParentCode, out var parent))
                    {
                        parentId = parent.MenuId;
                    }

                    if (menu.ParentMenuId != parentId &&
                        !updated.Contains(def.Code, StringComparer.OrdinalIgnoreCase) &&
                        !inserted.Contains(def.Code, StringComparer.OrdinalIgnoreCase))
                    {
                        updated.Add(def.Code);
                        unchanged.RemoveAll(c => string.Equals(c, def.Code, StringComparison.OrdinalIgnoreCase));
                    }
                }

                return BuildResult(inserted, updated, unchanged, disabled);
            }

            await db.SaveChangesAsync(cancellationToken);

            // Pass 2: resolve parents
            foreach (var def in flat.Values)
            {
                var menu = byCode[def.Code];
                int? parentId = null;
                if (def.ParentCode is not null)
                {
                    if (!byCode.TryGetValue(def.ParentCode, out var parent))
                    {
                        await tx!.RollbackAsync(cancellationToken);
                        return MenuSyncResult.Failed([$"Parent '{def.ParentCode}' not found for '{def.Code}'."]);
                    }

                    if (parent.MenuId == menu.MenuId)
                    {
                        await tx!.RollbackAsync(cancellationToken);
                        return MenuSyncResult.Failed([$"Invalid menu hierarchy: '{def.Code}' cannot be its own parent."]);
                    }

                    parentId = parent.MenuId;
                }

                if (menu.ParentMenuId != parentId)
                {
                    menu.ParentMenuId = parentId;
                    menu.ModifiedDate = DateTime.UtcNow;
                    menu.ModifiedBy = "XMLSYNC";
                    if (!updated.Contains(def.Code, StringComparer.OrdinalIgnoreCase) &&
                        !inserted.Contains(def.Code, StringComparer.OrdinalIgnoreCase))
                    {
                        updated.Add(def.Code);
                        unchanged.RemoveAll(c => string.Equals(c, def.Code, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            var cycleError = DetectDbCycles(byCode.Values.Where(m => flat.ContainsKey(m.MenuCode)));
            if (cycleError is not null)
            {
                await tx!.RollbackAsync(cancellationToken);
                return MenuSyncResult.Failed([cycleError]);
            }

            // Soft-disable missing
            foreach (var menu in existing)
            {
                if (!flat.ContainsKey(menu.MenuCode) && menu.IsActive)
                {
                    menu.IsActive = false;
                    menu.ModifiedDate = DateTime.UtcNow;
                    menu.ModifiedBy = "XMLSYNC";
                    disabled.Add(menu.MenuCode);
                }
            }

            var accessPermission = await db.Permissions
                .FirstAsync(p => p.PermissionCode == PermissionCodes.Access, cancellationToken);

            var existingAccessPairs = await db.MenuPermissions
                .Where(mp => mp.PermissionId == accessPermission.PermissionId)
                .Select(mp => mp.MenuId)
                .ToListAsync(cancellationToken);
            var accessSet = existingAccessPairs.ToHashSet();

            foreach (var code in inserted)
            {
                var menu = byCode[code];
                if (!accessSet.Contains(menu.MenuId))
                {
                    db.MenuPermissions.Add(new MenuPermission
                    {
                        MenuId = menu.MenuId,
                        PermissionId = accessPermission.PermissionId,
                        SortOrder = 1,
                        IsActive = true
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx!.CommitAsync(cancellationToken);
            _menuService.InvalidateCache();

            _logger.LogInformation(
                "Menu XML sync succeeded. Inserted={Inserted} Updated={Updated} Unchanged={Unchanged} Disabled={Disabled}",
                inserted.Count, updated.Count, unchanged.Count, disabled.Count);

            return BuildResult(inserted, updated, unchanged, disabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Menu XML sync failed");
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
            }

            return MenuSyncResult.Failed([ex.Message]);
        }
    }

    private static MenuSyncResult BuildResult(
        List<string> inserted,
        List<string> updated,
        List<string> unchanged,
        List<string> disabled) =>
        new()
        {
            Success = true,
            InsertedCount = inserted.Count,
            UpdatedCount = updated.Count,
            UnchangedCount = unchanged.Count,
            DisabledCount = disabled.Count,
            InsertedMenuCodes = inserted,
            UpdatedMenuCodes = updated,
            DisabledMenuCodes = disabled
        };

    private static string? DetectDbCycles(IEnumerable<Menu> menus)
    {
        var byId = menus.ToDictionary(m => m.MenuId);
        foreach (var menu in byId.Values)
        {
            var seen = new HashSet<int>();
            var current = menu;
            while (current.ParentMenuId is int parentId)
            {
                if (!seen.Add(current.MenuId))
                {
                    return $"Circular menu hierarchy involving '{menu.MenuCode}'.";
                }

                if (parentId == current.MenuId)
                {
                    return $"Invalid menu hierarchy: '{current.MenuCode}' cannot be its own parent.";
                }

                if (!byId.TryGetValue(parentId, out current!))
                {
                    break;
                }
            }
        }

        return null;
    }
}
