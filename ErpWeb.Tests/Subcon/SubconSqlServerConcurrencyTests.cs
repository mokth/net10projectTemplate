using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Security;
using ErpWeb.Core.Subcon;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Subcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Subcon;

[Collection("SubconSqlServer")]
[Trait(TestCategories.Name, TestCategories.Subcon)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class SubconSqlServerConcurrencyTests : IAsyncLifetime
{
    private const string TestConnectionKey = "SqlServerTestConnection";
    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;
    private string _company = "SC" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();
    private const string Branch = "HQ";

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        _connectionString = GetSqlServerConnectionString();
        if (_connectionString is null
            || !IsScratchDatabase(_connectionString)
            || !CanConnect(_connectionString))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        _factory = new SqlServerTestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = _company,
            SuppCode = "SUP01",
            SuppName = "Subcon Vendor",
            IsActive = true
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = _company,
            BranchCode = Branch,
            WarehouseCode = "MAIN",
            WarehouseDesc = "Main Store",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = _company,
            BranchCode = Branch,
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            LocDesc = "Bin 1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = _company,
            UomCode = "PCS",
            UomDesc = "Pieces",
            IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = _company,
            ICode = "RM1",
            IDesc = "Metal plate",
            StdUom = "PCS",
            StockControl = true,
            LotControl = false,
            DefWarehouse = "MAIN",
            DefLocation = "BIN1",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync();
        var receipts = await db.SubconReceipts.Include(x => x.Lines)
            .Where(x => x.CompanyCode == _company)
            .ToListAsync();
        db.SubconReceiptLines.RemoveRange(receipts.SelectMany(x => x.Lines));
        db.SubconReceipts.RemoveRange(receipts);

        var dispatches = await db.SubconDispatches.Include(x => x.Lines)
            .Where(x => x.CompanyCode == _company)
            .ToListAsync();
        db.SubconDispatchLines.RemoveRange(dispatches.SelectMany(x => x.Lines));
        db.SubconDispatches.RemoveRange(dispatches);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Concurrent_receipt_saves_against_same_dispatch_line_cannot_over_allocate()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var seedSvc = CreateService();
        var dispatch = await seedSvc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "RM1",
                    Qty = 5m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1",
                    Uom = "PCS"
                }
            ]
        });
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);

        var svc1 = CreateService();
        var svc2 = CreateService();
        var cmd1 = new SubconReceiptSaveCommand
        {
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = dispatch.Dispatch!.DocNo,
                    DispatchLineNo = 1,
                    Qty = 3m
                }
            ]
        };
        var cmd2 = new SubconReceiptSaveCommand
        {
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = dispatch.Dispatch.DocNo,
                    DispatchLineNo = 1,
                    Qty = 3m
                }
            ]
        };

        var results = await Task.WhenAll(
            svc1.SaveReceiptDraftAsync(cmd1),
            svc2.SaveReceiptDraftAsync(cmd2));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded));

        await using var db = await _factory!.CreateDbContextAsync();
        var totalAllocated = await db.SubconReceiptLines
            .Where(l => l.Dispatch.CompanyCode == _company && l.Dispatch.DocNo == dispatch.Dispatch.DocNo)
            .SumAsync(l => (decimal?)l.Qty) ?? 0m;
        Assert.Equal(3m, totalAllocated);
    }

    private SubconApplicationService CreateService()
    {
        var scope = new InventoryTenantScope
        {
            CompanyCode = _company,
            BranchCode = Branch,
            UserId = "tester"
        };
        var tenant = new Mock<IInventoryTenantContext>();
        tenant.Setup(x => x.TryCompanyScope()).Returns(scope);
        tenant.Setup(x => x.TryBranchScope()).Returns(scope);
        tenant.Setup(x => x.TryWriteScope()).Returns(scope);

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new SubconApplicationService(
            _factory!,
            tenant.Object,
            access.Object,
            new RunningNumberService(),
            new SubconBlockedInventorySourceAdapter(),
            new SubconBlockedProductionSourceAdapter());
    }

    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail("ERPWEB_REQUIRE_SQLSERVER_TESTS=1 is set, but SqlServerTestConnection is unavailable or not a scratch test DB.");
        }

        return true;
    }

    private sealed class SqlServerTestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString(TestConnectionKey);
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static bool IsScratchDatabase(string connectionString)
    {
        try
        {
            var name = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
            return name.Contains("test", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanConnect(string connectionString)
    {
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition("SubconSqlServer", DisableParallelization = true)]
public sealed class SubconSqlServerCollectionDefinition;
