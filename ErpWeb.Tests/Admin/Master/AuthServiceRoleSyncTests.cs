using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class AuthServiceRoleSyncTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AuthServiceRoleSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        db.Roles.Add(new Role { RoleId = 2, CompanyCode = "DEMO", RoleCode = "USER", RoleName = "User", IsActive = true });
        db.UserLogins.Add(new UserLogin
        {
            uid = 1,
            id = "clerk",
            name = "Clerk",
            password = PasswordHasher.Hash("Clerk@12"),
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = true,
            userlevel = "USER"
        });
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Failed_role_reconciliation_does_not_authenticate_user()
    {
        var hash = PasswordHasher.Hash("Clerk@12");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.FindByLoginAsync("DEMO", "clerk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserLogin
            {
                uid = 1,
                id = "clerk",
                name = "Clerk",
                password = hash,
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                UserID = "clerk",
                active = true,
                userlevel = "SALES" // role does not exist anywhere -> reconciliation fails
            });

        var sut = new AuthService(
            users.Object,
            _factory,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IPasswordPolicy>(),
            new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance),
            Mock.Of<ISecurityAuditWriter>(),
            NullLogger<AuthService>.Instance);

        var result = await sut.ValidateCredentialsAsync("DEMO", "clerk", "Clerk@12");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthService.GenericLoginFailure, result.ErrorMessage);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Valid_reconciliation_authenticates_user()
    {
        var hash = PasswordHasher.Hash("Clerk@12");
        var user = new UserLogin
        {
            uid = 1,
            id = "clerk",
            name = "Clerk",
            password = hash,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            UserID = "clerk",
            active = true,
            userlevel = "USER"
        };
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.FindByLoginAsync("DEMO", "clerk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        // Phase 8 final live-state recheck re-reads the same row; it must still match the verified hash.
        users.Setup(x => x.GetActiveByUidAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var sut = new AuthService(
            users.Object,
            _factory,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IPasswordPolicy>(),
            new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance),
            Mock.Of<ISecurityAuditWriter>(),
            NullLogger<AuthService>.Instance);

        var result = await sut.ValidateCredentialsAsync("DEMO", "clerk", "Clerk@12");

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.User);
    }
}
