using ErpWeb.Core.Services;

using ErpWeb.Model.Data;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;



namespace ErpWeb.Core.Menus;



public sealed class AccessRightService : IAccessRightService

{

    public const string AdminRole = "ADMIN";



    private readonly ICurrentUserService _currentUser;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private readonly ILogger<AccessRightService> _logger;

    private Dictionary<string, EffectiveMenuPermissions>? _cache;



    public AccessRightService(

        ICurrentUserService currentUser,

        IDbContextFactory<AppDbContext> dbFactory,

        ILogger<AccessRightService> logger)

    {

        _currentUser = currentUser;

        _dbFactory = dbFactory;

        _logger = logger;

    }



    public Task<bool> CanAccessAsync(string menuCode, CancellationToken cancellationToken = default) =>

        CanAsync(menuCode, PermissionCodes.Access, cancellationToken);



    public async Task<bool> CanAsync(string menuCode, string permissionCode, CancellationToken cancellationToken = default)

    {

        if (!_currentUser.IsAuthenticated)

        {

            return false;

        }



        if (_currentUser.IsInRole(AdminRole))

        {

            return true;

        }



        if (string.IsNullOrWhiteSpace(menuCode) || string.IsNullOrWhiteSpace(permissionCode))

        {

            return false;

        }



        if (!int.TryParse(_currentUser.SubjectUid, out _) ||

            string.IsNullOrWhiteSpace(_currentUser.CompanyCode))

        {

            return false;

        }



        var cache = await EnsureCacheAsync(cancellationToken);

        if (!cache.TryGetValue(menuCode, out var effective))

        {

            return false;

        }



        if (!string.Equals(permissionCode, PermissionCodes.Access, StringComparison.OrdinalIgnoreCase))

        {

            if (!effective.CanAccess)

            {

                return false;

            }

        }



        return effective.Permissions.Contains(permissionCode);

    }



    public async Task<IReadOnlySet<string>> GetPermissionsAsync(string menuCode, CancellationToken cancellationToken = default)

    {

        if (!_currentUser.IsAuthenticated)

        {

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        }



        if (_currentUser.IsInRole(AdminRole))

        {

            return new HashSet<string>(PermissionCodes.All, StringComparer.OrdinalIgnoreCase);

        }



        var cache = await EnsureCacheAsync(cancellationToken);

        return cache.TryGetValue(menuCode, out var effective)

            ? effective.Permissions

            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    }



    public async Task<MenuAccessRight?> GetAccessAsync(string menuCode, CancellationToken cancellationToken = default)

    {

        var permissions = await GetPermissionsAsync(menuCode, cancellationToken);

        if (permissions.Count == 0 && !_currentUser.IsInRole(AdminRole))

        {

            return null;

        }



        return new MenuAccessRight

        {

            MenuCode = menuCode,

            Permissions = permissions

        };

    }



    public async Task<IReadOnlyDictionary<string, MenuAccessRight>> GetAllAccessAsync(CancellationToken cancellationToken = default)

    {

        if (!_currentUser.IsAuthenticated)

        {

            return new Dictionary<string, MenuAccessRight>(StringComparer.OrdinalIgnoreCase);

        }



        if (_currentUser.IsInRole(AdminRole))

        {

            // ADMIN bypass: no per-menu DB cache; callers use GetPermissionsAsync for the catalog.

            return new Dictionary<string, MenuAccessRight>(StringComparer.OrdinalIgnoreCase);

        }



        var cache = await EnsureCacheAsync(cancellationToken);

        return cache.ToDictionary(

            kv => kv.Key,

            kv => new MenuAccessRight { MenuCode = kv.Key, Permissions = kv.Value.Permissions },

            StringComparer.OrdinalIgnoreCase);

    }



    public Task RefreshPermissionsAsync(CancellationToken cancellationToken = default)

    {

        _cache = null;

        return Task.CompletedTask;

    }



    private async Task<Dictionary<string, EffectiveMenuPermissions>> EnsureCacheAsync(CancellationToken cancellationToken)

    {

        if (_cache is not null)

        {

            return _cache;

        }



        if (!int.TryParse(_currentUser.SubjectUid, out var uid) ||

            string.IsNullOrWhiteSpace(_currentUser.CompanyCode))

        {

            _cache = new Dictionary<string, EffectiveMenuPermissions>(StringComparer.OrdinalIgnoreCase);

            return _cache;

        }



        var companyCode = _currentUser.CompanyCode;



        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);



        // Load all allow/deny rows that are applicable via MenuPermission.

        // Unknown menus/permissions and non-applicable grants never enter the result set.

        var rows = await (

            from map in db.UserRoleMappings.AsNoTracking()

            join role in db.Roles.AsNoTracking() on map.RoleId equals role.RoleId

            join rmp in db.RoleMenuPermissions.AsNoTracking() on role.RoleId equals rmp.RoleId

            join menu in db.Menus.AsNoTracking() on rmp.MenuId equals menu.MenuId

            join perm in db.Permissions.AsNoTracking() on rmp.PermissionId equals perm.PermissionId

            join mp in db.MenuPermissions.AsNoTracking()

                on new { rmp.MenuId, rmp.PermissionId } equals new { mp.MenuId, mp.PermissionId }

            where map.UserUid == uid

                  && role.CompanyCode == companyCode

                  && role.IsActive

                  && menu.IsActive

                  && perm.IsActive

                  && mp.IsActive

            select new

            {

                menu.MenuCode,

                perm.PermissionCode,

                rmp.IsAllowed

            }

        ).ToListAsync(cancellationToken);



        // Explicit DENY > ALLOW across multiple roles.

        var decisions = new Dictionary<(string Menu, string Permission), bool>(

            new MenuPermissionKeyComparer());



        foreach (var row in rows)

        {

            var key = (row.MenuCode, row.PermissionCode);

            if (!row.IsAllowed)

            {

                decisions[key] = false;

                continue;

            }



            if (!decisions.ContainsKey(key))

            {

                decisions[key] = true;

            }

        }



        var byMenu = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, allowed) in decisions)

        {

            if (!allowed)

            {

                continue;

            }



            if (!byMenu.TryGetValue(key.Menu, out var set))

            {

                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                byMenu[key.Menu] = set;

            }



            set.Add(key.Permission);

        }



        var cache = new Dictionary<string, EffectiveMenuPermissions>(StringComparer.OrdinalIgnoreCase);

        foreach (var (menuCode, permissions) in byMenu)

        {

            cache[menuCode] = new EffectiveMenuPermissions(menuCode, permissions);

        }



        _cache = cache;

        _logger.LogDebug(

            "Loaded permission cache for uid {UserUid} company {CompanyCode} ({MenuCount} menus)",

            uid, companyCode, cache.Count);

        return _cache;

    }



    private sealed class MenuPermissionKeyComparer : IEqualityComparer<(string Menu, string Permission)>

    {

        public bool Equals((string Menu, string Permission) x, (string Menu, string Permission) y) =>

            string.Equals(x.Menu, y.Menu, StringComparison.OrdinalIgnoreCase) &&

            string.Equals(x.Permission, y.Permission, StringComparison.OrdinalIgnoreCase);



        public int GetHashCode((string Menu, string Permission) obj) =>

            HashCode.Combine(

                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Menu),

                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Permission));

    }

}

