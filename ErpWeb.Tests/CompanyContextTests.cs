using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

public class CompanyContextTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public CompanyContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ResolveAsync_Maps_CompanyCode_And_HQ_Branch()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Companies.Add(new Company
            {
                CompanyCode = "DEMO",
                CompanyName = "Demo",
                IsActive = true,
                CurrencyCode = "MYR",
                TimeZoneId = "Asia/Kuala_Lumpur"
            });
            await db.SaveChangesAsync();
            var companyId = db.Companies.Single().CompanyId;
            db.Branches.Add(new Branch
            {
                CompanyId = companyId,
                BranchCode = "HQ",
                BranchName = "Head Office",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var currentUser = new StubCurrentUser("DEMO", "HQ", "MAIN");
        ICompanyContext context = new CompanyContext(currentUser, _factory);
        await context.ResolveAsync();

        Assert.Equal("DEMO", context.CompanyCode);
        Assert.Equal("HQ", context.BranchCode);
        Assert.Equal("MAIN", context.LegacyLocationCode);
        Assert.Equal("MYR", context.BaseCurrencyCode);
        Assert.Equal("Asia/Kuala_Lumpur", context.TimeZoneId);
        Assert.True(context.CompanyId > 0);
        Assert.True(context.BranchId > 0);
    }

    private sealed class StubCurrentUser : ICurrentUserService
    {
        public StubCurrentUser(string company, string branch, string location)
        {
            CompanyCode = company;
            BranchCode = branch;
            LocationCode = location;
        }

        public bool IsAuthenticated => true;
        public string? UserId => "admin";
        public string? LoginId => "admin";
        public string? FullName => "Admin";
        public string? CompanyCode { get; }
        public string? BranchCode { get; }
        public string? LocationCode { get; }
        public string? UserLevel => "SYSTEM_ADMIN";
        public bool MustChangePassword => false;
        public string? SubjectUid => "1";
        public bool IsInRole(string role) => true;
    }
}
