using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Tests.Infrastructure.Helpers;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Sales.Master;

[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesMasters)]
public sealed class SaPriceMaintenanceServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaPriceMaintenanceServiceTests()
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

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "ACTIVE",
                IDesc = "Active item",
                IsActive = true,
                SellingUom = "PCS",
                SellingPrice = 10m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 1]
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "INACTIVE",
                IDesc = "Inactive item",
                IsActive = false,
                SellingUom = "PCS",
                SellingPrice = 20m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 2]
            },
            new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "OTHER-ITEM",
                IDesc = "Other company item",
                IsActive = true,
                SellingUom = "PCS",
                SellingPrice = 30m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 3]
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "BLANK-PRICE",
                IDesc = "Blank price item",
                IsActive = true,
                StdUom = "PCS",
                SellingPrice = null,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 5]
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "SELLING-UOM",
                IDesc = "Selling UOM item",
                IsActive = true,
                SellingUom = "BOX",
                StdUom = "PCS",
                SellingPrice = 40m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 6]
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "INACTIVE-BLANK",
                IDesc = "Inactive blank price item",
                IsActive = false,
                StdUom = "PCS",
                SellingPrice = null,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 7]
            });
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL-1",
            CustPriceDesc = "Demo price list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 4]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST-1",
            CustName = "Direct price-list customer",
            CustPriceCode = "PL-1",
            IsActive = true
        });
        db.SaCustGroups.Add(new SaCustGroup
        {
            CompanyCode = "DEMO",
            CustGroupCode = "GROUP-1",
            CustGroupDesc = "Default price-list group",
            CustPriceCode = "PL-1"
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Load_all_active_items_includes_blank_prices_and_uses_effective_uom()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            LoadAllActiveItems = true,
            ActiveItemsOnly = false
        });

        Assert.True(result.Succeeded, result.Message);
        var rows = result.Data!.Rows.ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, rows.Count);
        Assert.Contains("ACTIVE", rows.Keys);
        Assert.Contains("BLANK-PRICE", rows.Keys);
        Assert.Contains("SELLING-UOM", rows.Keys);
        Assert.DoesNotContain("INACTIVE", rows.Keys);
        Assert.DoesNotContain("INACTIVE-BLANK", rows.Keys);
        Assert.Null(rows["BLANK-PRICE"].CurrentPrice);
        Assert.Equal("PCS", rows["BLANK-PRICE"].Uom);
        Assert.Equal("BOX", rows["SELLING-UOM"].Uom);
    }

    [Fact]
    public async Task Preview_allows_initial_price_for_blank_item_default()
    {
        var row = await LoadBlankPriceRowAsync();
        var result = await CreateSut().PreviewAsync(PreviewRequest(
            row,
            SaPriceAdjustmentMethods.SetPrice,
            150m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.Summary.Changing);
        Assert.Equal(0, result.Data.Summary.Blocked);
        Assert.Equal(150m, Assert.Single(result.Data.Rows).ProposedPrice);
    }

    [Fact]
    public async Task Preview_explains_that_relative_adjustment_needs_initial_price()
    {
        var row = await LoadBlankPriceRowAsync();
        var result = await CreateSut().PreviewAsync(PreviewRequest(
            row,
            SaPriceAdjustmentMethods.IncreasePercent,
            null));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.Summary.Blocked);
        Assert.Contains("Enter the initial selling price", Assert.Single(result.Data.Rows).Warning);
    }

    [Fact]
    public async Task Apply_initializes_blank_price_and_audits_effective_uom()
    {
        var row = await LoadBlankPriceRowAsync();
        var result = await CreateSut().ApplyAsync(ApplyRequest(row, 150m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.ChangedRowCount);

        await using var db = await _factory.CreateDbContextAsync();
        var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "BLANK-PRICE");
        Assert.Equal(150m, item.SellingPrice);

        var batch = await db.SaPriceChangeBatches
            .Include(x => x.Lines)
            .SingleAsync(x => x.PriceChangeBatchId == result.Data.PriceChangeBatchId);
        var line = Assert.Single(batch.Lines);
        Assert.Null(line.OldPrice);
        Assert.Equal(150m, line.NewPrice);
        Assert.Equal("PCS", line.OldUom);
        Assert.Equal("PCS", line.NewUom);
    }

    [Fact]
    public async Task Apply_rejects_a_null_baseline_that_changed_after_review()
    {
        var row = await LoadBlankPriceRowAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "BLANK-PRICE");
            item.SellingPrice = 120m;
            await db.SaveChangesAsync();
        }

        var result = await CreateSut().ApplyAsync(ApplyRequest(row, 150m));

        Assert.False(result.Succeeded);
        await using var verifyDb = await _factory.CreateDbContextAsync();
        var unchanged = await verifyDb.IvStockMasters
            .SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "BLANK-PRICE");
        Assert.Equal(120m, unchanged.SellingPrice);
    }

    [Fact]
    public async Task Workbook_keeps_relative_new_price_blank_and_accepts_manual_initial_price()
    {
        var service = CreateSut();
        var row = await LoadBlankPriceRowAsync();
        var export = await service.BuildReviewWorkbookAsync(new SaPriceReviewExportRequest
        {
            Query = new SaPriceReviewQuery
            {
                TargetType = SaPriceMaintenanceTargets.ItemDefault,
                ItemCode = row.ItemCode
            },
            AdjustmentMethod = SaPriceAdjustmentMethods.IncreasePercent,
            AdjustmentValue = 10m,
            DecimalPlaces = 2
        });

        Assert.True(export.Succeeded, export.Message);
        using var exportedStream = new MemoryStream(export.Data!);
        using var workbook = new XLWorkbook(exportedStream);
        var sheet = workbook.Worksheet("Prices");
        var columns = sheet.Row(1).CellsUsed().ToDictionary(
            x => x.GetString(),
            x => x.Address.ColumnNumber,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(sheet.Cell(2, columns["Current Price"]).IsEmpty());
        Assert.True(sheet.Cell(2, columns["New Price"]).IsEmpty());

        sheet.Cell(2, columns["New Price"]).Value = 75m;
        sheet.Cell(2, columns["Selected"]).Value = "Yes";
        using var importedStream = new MemoryStream();
        workbook.SaveAs(importedStream);
        importedStream.Position = 0;

        var imported = await service.ParseImportAsync(importedStream, new SaPriceImportContext
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            StagedRows = [row]
        });

        Assert.True(imported.Succeeded, imported.Message);
        Assert.True(imported.Data!.IsValid);
        var update = Assert.Single(imported.Data.Updates);
        Assert.True(update.Selected);
        Assert.Equal(75m, update.NewPrice);
    }

    [Fact]
    public async Task Irrelevant_customer_filter_cannot_open_unbounded_item_default_review()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            CustCode = "CUST-ONLY"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("scope filter", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Irrelevant_price_list_filter_cannot_open_unbounded_customer_special_review()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.CustomerItem,
            CustPriceCode = "PL-ONLY"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("scope filter", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Price_list_impact_is_company_scoped_and_counts_assignments()
    {
        var result = await CreateSut().GetPriceListImpactAsync("PL-1");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("PL-1", result.Data!.CustPriceCode);
        Assert.Equal(1, result.Data.CustomersAssignedDirectly);
        Assert.Equal(1, result.Data.CustomerGroupsUsingAsDefault);
    }

    private SaPriceMaintenanceService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = new Mock<ITenantScopeContext>();
        tenant.Setup(x => x.TryCompanyScope()).Returns(Scope());
        tenant.Setup(x => x.TryWriteScope()).Returns(Scope());

        return new SaPriceMaintenanceService(
            _factory,
            tenant.Object,
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 10)));
    }

    private async Task<SaPriceReviewRow> LoadBlankPriceRowAsync()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            ItemCode = "BLANK-PRICE"
        });

        Assert.True(result.Succeeded, result.Message);
        return Assert.Single(result.Data!.Rows);
    }

    private static SaPricePreviewRequest PreviewRequest(
        SaPriceReviewRow row,
        string method,
        decimal? newPrice) => new()
    {
        TargetType = SaPriceMaintenanceTargets.ItemDefault,
        AdjustmentMethod = method,
        AdjustmentValue = method == SaPriceAdjustmentMethods.SetPrice ? newPrice ?? 0m : 10m,
        DecimalPlaces = 2,
        Selections =
        [
            new SaPriceReviewSelection
            {
                ReviewRowKey = row.ReviewRowKey,
                BaselinePrice = row.CurrentPrice,
                NewPrice = newPrice,
                RowVersion = row.RowVersion
            }
        ]
    };

    private static SaPriceApplyRequest ApplyRequest(SaPriceReviewRow row, decimal newPrice) => new()
    {
        TargetType = SaPriceMaintenanceTargets.ItemDefault,
        AdjustmentMethod = SaPriceAdjustmentMethods.SetPrice,
        AdjustmentValue = newPrice,
        DecimalPlaces = 2,
        Reason = "Initialize blank Item Default price",
        ReviewScope = new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            ItemCode = row.ItemCode
        },
        Selections =
        [
            new SaPriceReviewSelection
            {
                ReviewRowKey = row.ReviewRowKey,
                BaselinePrice = row.CurrentPrice,
                NewPrice = newPrice,
                RowVersion = row.RowVersion
            }
        ]
    };

    private static TenantScope Scope() => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        LocationCode = "SITE",
        UserId = "admin"
    };
}
