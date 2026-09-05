using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

public sealed class SaPaymentTermSalesRepMappingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SaPaymentTermSalesRepMappingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task PaymentTerm_RoundTrips_CompanyScopedKeyAndActiveColumn()
    {
        await using (var db = new AppDbContext(_options))
        {
            db.SaPaymentTerms.Add(new SaPaymentTerm
            {
                CompanyCode = "DEMO",
                PayCode = "NET30",
                PayDesc = "Net 30 days",
                Days = 30,
                IsActive = true,
                CreatedBy = "SEED"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(_options))
        {
            var row = await db.SaPaymentTerms.AsNoTracking()
                .SingleAsync(x => x.CompanyCode == "DEMO" && x.PayCode == "NET30");
            Assert.Equal("Net 30 days", row.PayDesc);
            Assert.Equal(30, row.Days);
            Assert.True(row.IsActive);
            Assert.Equal("SEED", row.CreatedBy);
        }
    }

    [Fact]
    public async Task SalesRep_RoundTrips_SRepColumnsAndCommissionRate()
    {
        await using (var db = new AppDbContext(_options))
        {
            db.SaSalesReps.Add(new SaSalesRep
            {
                CompanyCode = "DEMO",
                SrepCode = "SM1",
                SrepName = "Sample Sales Rep",
                Email = "sm1@example.com",
                CommissionRate = 5.5m,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(_options))
        {
            var row = await db.SaSalesReps.AsNoTracking()
                .SingleAsync(x => x.CompanyCode == "DEMO" && x.SrepCode == "SM1");
            Assert.Equal("Sample Sales Rep", row.SrepName);
            Assert.Equal("sm1@example.com", row.Email);
            Assert.Equal(5.5m, row.CommissionRate);
            Assert.True(row.IsActive);
        }
    }
}
