using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class MenuSyncServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public MenuSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Permissions.Add(new Permission
        {
            PermissionCode = PermissionCodes.Access,
            PermissionName = "Access",
            PermissionType = "Navigation",
            SortOrder = 1,
            IsActive = true
        });
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Deep_parent_synchronization()
    {
        var defs = CreateDefinitions(
            ("OPERATIONS", "Operations", 1, null, null),
            ("OVERVIEW", "Overview", 1, "OPERATIONS", null),
            ("DASHBOARD", "Dashboard", 1, "OVERVIEW", "/dashboard"));

        var result = await CreateSut(defs).SyncFromXmlAsync();
        Assert.True(result.Success);

        await using var db = await _factory.CreateDbContextAsync();
        var menus = await db.Menus.ToDictionaryAsync(m => m.MenuCode);
        Assert.Null(menus["OPERATIONS"].ParentMenuId);
        Assert.Equal(menus["OPERATIONS"].MenuId, menus["OVERVIEW"].ParentMenuId);
        Assert.Equal(menus["OVERVIEW"].MenuId, menus["DASHBOARD"].ParentMenuId);
    }

    [Fact]
    public async Task Preserves_MenuId_on_reorder_and_rename()
    {
        Dictionary<string, int> before;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Menus.AddRange(
                new Menu { MenuCode = "A", MenuName = "A", Route = "/a", SortOrder = 1, IsActive = true },
                new Menu { MenuCode = "B", MenuName = "B", Route = "/b", SortOrder = 2, IsActive = true },
                new Menu { MenuCode = "C", MenuName = "C", Route = "/c", SortOrder = 3, IsActive = true });
            await db.SaveChangesAsync();
            before = await db.Menus.ToDictionaryAsync(m => m.MenuCode, m => m.MenuId);
        }

        var defs = CreateDefinitions(
            ("C", "Charlie", 1, null, "/c"),
            ("A", "Alpha", 2, null, "/a"),
            ("B", "Bravo", 3, null, "/b"));

        var result = await CreateSut(defs).SyncFromXmlAsync();
        Assert.True(result.Success);

        await using var verify = await _factory.CreateDbContextAsync();
        var menus = await verify.Menus.ToDictionaryAsync(m => m.MenuCode);
        Assert.Equal(before["A"], menus["A"].MenuId);
        Assert.Equal(before["B"], menus["B"].MenuId);
        Assert.Equal(before["C"], menus["C"].MenuId);
        Assert.Equal("Alpha", menus["A"].MenuName);
    }

    [Fact]
    public async Task Reparent_preserves_MenuId()
    {
        int bId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var a = new Menu { MenuCode = "A", MenuName = "A", Route = null, SortOrder = 1, IsActive = true };
            var c = new Menu { MenuCode = "C", MenuName = "C", Route = null, SortOrder = 2, IsActive = true };
            db.Menus.AddRange(a, c);
            await db.SaveChangesAsync();
            var b = new Menu
            {
                MenuCode = "B",
                MenuName = "B",
                Route = "/b",
                ParentMenuId = a.MenuId,
                SortOrder = 1,
                IsActive = true
            };
            db.Menus.Add(b);
            await db.SaveChangesAsync();
            bId = b.MenuId;
        }

        var defs = CreateDefinitions(
            ("A", "A", 1, null, null),
            ("C", "C", 2, null, null),
            ("B", "B", 1, "C", "/b"));

        var result = await CreateSut(defs).SyncFromXmlAsync();
        Assert.True(result.Success);

        await using var verify = await _factory.CreateDbContextAsync();
        var menus = await verify.Menus.ToDictionaryAsync(m => m.MenuCode);
        Assert.Equal(bId, menus["B"].MenuId);
        Assert.Equal(menus["C"].MenuId, menus["B"].ParentMenuId);
    }

    [Fact]
    public async Task Sync_is_idempotent()
    {
        var defs = CreateDefinitions(
            ("OPERATIONS", "Operations", 1, null, null),
            ("OVERVIEW", "Overview", 1, "OPERATIONS", null),
            ("DASHBOARD", "Dashboard", 1, "OVERVIEW", "/dashboard"));

        var sut = CreateSut(defs);
        var first = await sut.SyncFromXmlAsync();
        Assert.True(first.Success);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var snapshot = await db.Menus.AsNoTracking()
                .Select(m => new { m.MenuId, m.MenuCode, m.ParentMenuId, m.SortOrder, m.IsActive })
                .OrderBy(m => m.MenuCode)
                .ToListAsync();
            var permCount = await db.MenuPermissions.CountAsync();

            var second = await sut.SyncFromXmlAsync();
            Assert.True(second.Success);
            Assert.Equal(0, second.InsertedCount);
            Assert.Equal(0, second.UpdatedCount);
            Assert.Equal(0, second.DisabledCount);

            var after = await db.Menus.AsNoTracking()
                .Select(m => new { m.MenuId, m.MenuCode, m.ParentMenuId, m.SortOrder, m.IsActive })
                .OrderBy(m => m.MenuCode)
                .ToListAsync();
            Assert.Equal(snapshot, after);
            Assert.Equal(permCount, await db.MenuPermissions.CountAsync());
        }
    }

    [Fact]
    public async Task Soft_disables_removed_group_and_preserves_permissions()
    {
        var seed = CreateDefinitions(
            ("OPERATIONS", "Operations", 1, null, null),
            ("OVERVIEW", "Overview", 1, "OPERATIONS", null),
            ("DASHBOARD", "Dashboard", 1, "OVERVIEW", "/dashboard"));
        await CreateSut(seed).SyncFromXmlAsync();

        int overviewId;
        int permCount;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            overviewId = await db.Menus.Where(m => m.MenuCode == "OVERVIEW").Select(m => m.MenuId).SingleAsync();
            permCount = await db.MenuPermissions.CountAsync(mp => mp.MenuId == overviewId);
            Assert.True(permCount > 0);
        }

        var withoutOverview = CreateDefinitions(
            ("OPERATIONS", "Operations", 1, null, null),
            ("DASHBOARD", "Dashboard", 1, "OPERATIONS", "/dashboard"));
        var result = await CreateSut(withoutOverview).SyncFromXmlAsync();
        Assert.True(result.Success);
        Assert.Contains("OVERVIEW", result.DisabledMenuCodes);

        await using var verify = await _factory.CreateDbContextAsync();
        var overview = await verify.Menus.SingleAsync(m => m.MenuCode == "OVERVIEW");
        Assert.False(overview.IsActive);
        Assert.Equal(overviewId, overview.MenuId);
        Assert.Equal(permCount, await verify.MenuPermissions.CountAsync(mp => mp.MenuId == overviewId));
    }

    [Fact]
    public async Task Soft_disables_removed_leaf_and_preserves_permissions()
    {
        await CreateSut(CreateDefinitions(("KEEP", "Keep", 1, null, "/keep"), ("GONE", "Gone", 2, null, "/gone")))
            .SyncFromXmlAsync();

        int goneId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            goneId = await db.Menus.Where(m => m.MenuCode == "GONE").Select(m => m.MenuId).SingleAsync();
        }

        var result = await CreateSut(CreateDefinitions(("KEEP", "Keep", 1, null, "/keep"))).SyncFromXmlAsync();
        Assert.True(result.Success);

        await using var verify = await _factory.CreateDbContextAsync();
        var gone = await verify.Menus.SingleAsync(m => m.MenuCode == "GONE");
        Assert.False(gone.IsActive);
        Assert.True(await verify.MenuPermissions.AnyAsync(mp => mp.MenuId == goneId));
    }

    [Fact]
    public async Task Inserts_new_menu_with_ACCESS_only_MenuPermission()
    {
        var result = await CreateSut(CreateDefinitions(("NEW_MENU", "New", 1, null, "/new"))).SyncFromXmlAsync();
        Assert.True(result.Success);

        await using var db = await _factory.CreateDbContextAsync();
        var menu = await db.Menus.SingleAsync(m => m.MenuCode == "NEW_MENU");
        var perms = await db.MenuPermissions.Include(mp => mp.Permission)
            .Where(mp => mp.MenuId == menu.MenuId).ToListAsync();
        Assert.Single(perms);
        Assert.Equal(PermissionCodes.Access, perms[0].Permission.PermissionCode);
    }

    [Fact]
    public async Task Preview_does_not_modify_database()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Menus.Add(new Menu { MenuCode = "A", MenuName = "A", Route = "/a", SortOrder = 1, IsActive = true });
            await db.SaveChangesAsync();
        }

        var preview = await CreateSut(CreateDefinitions(("B", "B", 1, null, "/b"))).PreviewXmlSyncAsync();
        Assert.True(preview.Success);
        Assert.Equal(1, preview.InsertedCount);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.False(await verify.Menus.AnyAsync(m => m.MenuCode == "B"));
    }

    [Fact]
    public async Task Sync_failure_does_not_partially_commit()
    {
        var bad = CreateDefinitions(
            ("CHILD", "Child", 1, "MISSING_PARENT", "/child"));

        var beforeCount = 0;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            beforeCount = await db.Menus.CountAsync();
        }

        var result = await CreateSut(bad).SyncFromXmlAsync();
        Assert.False(result.Success);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(beforeCount, await verify.Menus.CountAsync());
        Assert.False(await verify.Menus.AnyAsync(m => m.MenuCode == "CHILD"));
    }

    private MenuSyncService CreateSut(IMenuDefinitionService defs) =>
        new(defs, new MenuService(_factory, new MenuCache(), NullLogger<MenuService>.Instance),
            _factory, NullLogger<MenuSyncService>.Instance);

    private static IMenuDefinitionService CreateDefinitions(
        params (string Code, string Name, int Sort, string? Parent, string? Route)[] items)
    {
        var flat = items.ToDictionary(
            i => i.Code,
            i => new MenuDefinitionNode
            {
                Code = i.Code,
                Name = i.Name,
                Route = i.Route,
                SortOrder = i.Sort,
                AlwaysVisible = false,
                ParentCode = i.Parent,
                Children = Array.Empty<MenuDefinitionNode>()
            },
            StringComparer.OrdinalIgnoreCase);

        // Rebuild Children for IsGroup accuracy if needed by callers (sync uses ParentCode)
        var withChildren = flat.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var children = items
                    .Where(i => string.Equals(i.Parent, kv.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(i => flat[i.Code])
                    .ToList();
                return new MenuDefinitionNode
                {
                    Code = kv.Value.Code,
                    Name = kv.Value.Name,
                    Route = kv.Value.Route,
                    SortOrder = kv.Value.SortOrder,
                    AlwaysVisible = kv.Value.AlwaysVisible,
                    ParentCode = kv.Value.ParentCode,
                    Children = children
                };
            },
            StringComparer.OrdinalIgnoreCase);

        var mock = new Mock<IMenuDefinitionService>();
        mock.Setup(x => x.Validate()).Returns(Array.Empty<string>());
        mock.Setup(x => x.GetFlatByCode()).Returns(withChildren);
        mock.Setup(x => x.GetTree()).Returns(withChildren.Values.Where(v => v.ParentCode is null).ToList());
        return mock.Object;
    }
}
