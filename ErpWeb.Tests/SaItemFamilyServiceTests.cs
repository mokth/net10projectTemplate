using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Sales item family (plan plans/sales-item-family-v2-plan.md §24):
/// price lists + lines, customer items and item discount rules — including the two rules that carry the
/// most risk: VIEW_PRICE never changes what is stored (§11.1) and two discount rules must never be able
/// to match the same item (§8.3).
/// </summary>
public class SaItemFamilyServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 15);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaItemFamilyServiceTests()
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

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "I1",
            IDesc = "Item one",
            IClassCode = "C1",
            StdUom = "PCS",
            SellingUom = "PCS",
            IsActive = true,
            RowVersion = Rv(1)
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "I2",
            IDesc = "Item two",
            IClassCode = "C1",
            StdUom = "PCS",
            SellingUom = "PCS",
            IsActive = true,
            RowVersion = Rv(2)
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "OTHER",
            ICode = "I1",
            IDesc = "Other company item",
            IsActive = true,
            RowVersion = Rv(3)
        });

        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "PCS", UomDesc = "Pieces", IsActive = true, RowVersion = Rv(4) });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "C1", IDesc = "Class one", RowVersion = Rv(5) });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST1",
            CustName = "Customer one",
            RowVersion = Rv(6)
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    // ===================== IvCustPriceGroup =====================

    [Fact]
    public async Task PriceGroup_Save_WithLines_PersistsAggregate_AndStampsTenant()
    {
        var sut = CreateSut(canViewPrice: true);

        var result = await sut.SaveCustPriceGroupAsync(new IvCustPriceGroupEditVm
        {
            CustPriceCode = "PL1",
            CustPriceDesc = "Price list 1",
            IsActive = true,
            Lines =
            [
                new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 12.50m },
                new IvCustPriceLineVm { ICode = "I2", UOM = "PCS", SellingPrice = 20.00m }
            ]
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var header = await db.IvCustPriceGroups.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustPriceCode == "PL1");
        Assert.Equal("PRICE LIST 1", header.CustPriceDesc);
        Assert.Equal("HQ", header.BranchCode);
        Assert.Equal("SITE", header.LocationCode);

        var lines = await db.IvCustPrices.Where(x => x.CompanyCode == "DEMO" && x.CustPriceCode == "PL1").ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Equal(12.50m, lines.Single(x => x.ICode == "I1").SellingPrice);
    }

    [Fact]
    public async Task PriceGroup_Save_RejectsUnknownItem()
    {
        var sut = CreateSut();

        var result = await sut.SaveCustPriceGroupAsync(new IvCustPriceGroupEditVm
        {
            CustPriceCode = "PL1",
            CustPriceDesc = "Price list 1",
            Lines = [new IvCustPriceLineVm { ICode = "NOPE", UOM = "PCS", SellingPrice = 1m }]
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("does not exist", result.ValidationErrors["Lines[0].ICode"]);
    }

    [Fact]
    public async Task PriceGroup_Save_RejectsDuplicateItemAndUomInPayload()
    {
        var sut = CreateSut();

        var result = await sut.SaveCustPriceGroupAsync(new IvCustPriceGroupEditVm
        {
            CustPriceCode = "PL1",
            CustPriceDesc = "Price list 1",
            Lines =
            [
                new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 1m },
                new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 2m }
            ]
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("more than once", result.ValidationErrors["Lines[1].ICode"]);
    }

    [Fact]
    public async Task PriceGroup_Edit_WithStaleRowVersion_ReturnsConcurrency()
    {
        var sut = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();

        var result = await sut.SaveCustPriceGroupAsync(new IvCustPriceGroupEditVm
        {
            CustPriceCode = "PL1",
            CustPriceDesc = "Changed",
            IsActive = true,
            RowVersion = Rv(99),
            Lines = [new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 9m }]
        }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task PriceGroup_Save_ReplaceLines_RemovesDroppedLine()
    {
        var sut = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();

        var result = await sut.SaveCustPriceGroupAsync(new IvCustPriceGroupEditVm
        {
            CustPriceCode = "PL1",
            CustPriceDesc = "Price list 1",
            IsActive = true,
            RowVersion = Rv(11),
            Lines = [new IvCustPriceLineVm { ICode = "I2", UOM = "PCS", SellingPrice = 7m }]
        }, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var lines = await db.IvCustPrices.Where(x => x.CustPriceCode == "PL1").ToListAsync();
        Assert.Single(lines);
        Assert.Equal("I2", lines[0].ICode);
    }

    [Fact]
    public async Task PriceGroup_Delete_BlockedByLines()
    {
        var sut = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();

        var check = await sut.CanDeleteCustPriceGroupsAsync(["PL1"]);

        Assert.False(check.CanDelete);
        Assert.Contains(check.References, r => r.ReferenceType == "IvCustPrice");
    }

    [Fact]
    public async Task PriceGroup_Delete_BlockedByCustomerAssignment()
    {
        var sut = CreateSut();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL1",
                CustPriceDesc = "PRICE LIST 1",
                IsActive = true,
                RowVersion = Rv(21)
            });
            var cust = await db.SaCusts.SingleAsync(x => x.CustCode == "CUST1");
            cust.CustPriceCode = "PL1";
            await db.SaveChangesAsync();
        }

        var check = await sut.CanDeleteCustPriceGroupsAsync(["PL1"]);

        Assert.False(check.CanDelete);
        Assert.Contains(check.References, r => r.ReferenceType == "SaCust.CustPriceCode");
    }

    [Fact]
    public async Task PriceGroup_Delete_Unreferenced_Succeeds()
    {
        var sut = CreateSut();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL9",
                CustPriceDesc = "EMPTY LIST",
                IsActive = true,
                RowVersion = Rv(22)
            });
            await db.SaveChangesAsync();
        }

        var check = await sut.CanDeleteCustPriceGroupsAsync(["PL9"]);
        Assert.True(check.CanDelete);

        var result = await sut.DeleteCustPriceGroupsAsync([
            new SaItemFamilyKeyToken { Key = "PL9", RowVersion = Rv(22) }
        ]);

        Assert.True(result.Succeeded, result.Message);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.False(await verify.IvCustPriceGroups.AnyAsync(x => x.CustPriceCode == "PL9"));
    }

    [Fact]
    public async Task PriceGroup_Delete_WithStaleRowVersion_ReturnsConcurrency()
    {
        var sut = CreateSut();
        await SeedPriceGroupAsync();

        var result = await sut.DeleteCustPriceGroupsAsync([
            new SaItemFamilyKeyToken { Key = "PL1", RowVersion = Rv(99) }
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.IvCustPriceGroups.AnyAsync(x => x.CustPriceCode == "PL1"));
    }

    // ===================== VIEW_PRICE (§11.1) =====================

    [Fact]
    public async Task ViewPrice_Denied_HidesPrices_ButKeepsStoredValue()
    {
        var allowed = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();

        var denied = CreateSut(canViewPrice: false);
        var deniedRead = await denied.GetCustPriceGroupAsync("PL1");
        Assert.True(deniedRead.Succeeded, deniedRead.Message);
        Assert.Null(deniedRead.Data!.Lines[0].SellingPrice);

        var allowedRead = await allowed.GetCustPriceGroupAsync("PL1");
        Assert.Equal(12.50m, allowedRead.Data!.Lines[0].SellingPrice);

        // The stored value is untouched by the denial — visibility is never persistence.
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(12.50m, (await db.IvCustPrices.SingleAsync(x => x.CustPriceCode == "PL1")).SellingPrice);
    }

    [Fact]
    public async Task ViewPrice_Denied_HidesCustomerItemPrice_ButSaveStillPersistsIt()
    {
        var sut = CreateSut(canViewPrice: true);
        var saved = await sut.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "CUST-PART-1",
            SellingUOM = "PCS",
            MOQ = 0,
            UnitPrice = 12.34m
        }, isNew: true);
        Assert.True(saved.Succeeded, saved.Message);

        var denied = CreateSut(canViewPrice: false);
        var deniedRead = await denied.GetItemCustAsync(new SaItemCustKey
        {
            CustCode = "CUST1",
            ICode = "I1",
            SellingUOM = "PCS",
            MOQ = 0
        });
        Assert.True(deniedRead.Succeeded, deniedRead.Message);
        Assert.Null(deniedRead.Data!.UnitPrice);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaItemCusts.SingleAsync(x => x.CustCode == "CUST1" && x.ICode == "I1");
        Assert.Equal(12.34, row.UnitPrice!.Value, 4);
    }

    [Fact]
    public async Task ViewPrice_Denied_EditDoesNotEraseStoredLinePrice()
    {
        var denied = CreateSut(canViewPrice: false);
        await SeedPriceGroupAsync();

        // The caller cannot see the price, so their payload has none — and the stored one must survive.
        var result = await denied.SaveCustPriceGroupAsync(new IvCustPriceGroupEditVm
        {
            CustPriceCode = "PL1",
            CustPriceDesc = "Price list 1",
            IsActive = true,
            RowVersion = Rv(11),
            Lines = [new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = null }]
        }, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.IvCustPrices.SingleAsync(x => x.CustPriceCode == "PL1");
        Assert.Equal(12.50m, line.SellingPrice);
    }

    [Fact]
    public async Task ViewPrice_Denied_EditDoesNotEraseStoredItemPrice()
    {
        var denied = CreateSut(canViewPrice: false);
        await SeedItemCustAsync();

        var result = await denied.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "CUST-PART-1",
            SellingUOM = "PCS",
            MOQ = 0,
            UnitPrice = null,
            RowVersion = Rv(31)
        }, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaItemCusts.SingleAsync(x => x.CustCode == "CUST1" && x.ICode == "I1");
        Assert.Equal(12.34, row.UnitPrice!.Value, 4);
    }

    [Fact]
    public async Task ViewPrice_Denied_CannotSetPriceOnNewRow()
    {
        var denied = CreateSut(canViewPrice: false);

        var result = await denied.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "CUST-PART-1",
            SellingUOM = "PCS",
            MOQ = 0,
            UnitPrice = 999m
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaItemCusts.SingleAsync(x => x.CustCode == "CUST1" && x.ICode == "I1");
        Assert.Null(row.UnitPrice);
    }

    // ===================== SaItemCust =====================

    [Fact]
    public async Task ItemCust_Save_New_DefaultsStatusNew()
    {
        var sut = CreateSut(canViewPrice: true);

        var result = await sut.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "CUST-PART-1",
            SellingUOM = "PCS",
            MOQ = 0
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaItemCusts.SingleAsync(x => x.CustCode == "CUST1");
        Assert.Equal("NEW", row.Status);
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task ItemCust_Save_UnknownCustomer_Rejected()
    {
        var sut = CreateSut();

        var result = await sut.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "NOPE",
            ICode = "I1",
            CustICode = "X",
            SellingUOM = "PCS"
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.ValidationErrors["CustCode"]);
    }

    [Fact]
    public async Task ItemCust_Save_UnknownUom_Rejected()
    {
        var sut = CreateSut();

        var result = await sut.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "X",
            SellingUOM = "BOX"
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.ValidationErrors["SellingUOM"]);
    }

    [Fact]
    public async Task ItemCust_Save_DuplicateKey_Rejected()
    {
        var sut = CreateSut();
        await SeedItemCustAsync();

        var result = await sut.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "X",
            SellingUOM = "PCS",
            MOQ = 0
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("already exists", result.ValidationErrors["MOQ"]);
    }

    [Fact]
    public async Task ItemCust_Edit_KeyChange_IsRejected()
    {
        var sut = CreateSut();
        await SeedItemCustAsync();

        // Same customer/item but a different MOQ: the identity changed, so this is not an edit of the
        // existing row (the legacy defect being removed).
        var result = await sut.SaveItemCustAsync(new SaItemCustEditVm
        {
            CustCode = "CUST1",
            ICode = "I1",
            CustICode = "X",
            SellingUOM = "PCS",
            MOQ = 5,
            RowVersion = Rv(31)
        }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot be changed", result.ValidationErrors["ICode"]);
    }

    [Fact]
    public async Task ItemCust_Delete_NoConsumerYet_IsAnExplicitNote()
    {
        var sut = CreateSut();
        await SeedItemCustAsync();

        var check = await sut.CanDeleteItemCustsAsync([
            new SaItemCustKey { CustCode = "CUST1", ICode = "I1", SellingUOM = "PCS", MOQ = 0 }
        ]);

        Assert.True(check.CanDelete);
        Assert.Contains("No module consumes", check.Message);

        var result = await sut.DeleteItemCustsAsync([
            new SaItemFamilyKeyToken { Key = "CUST1;I1;PCS;0", RowVersion = Rv(31) }
        ]);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaItemCusts.AnyAsync(x => x.CustCode == "CUST1"));
    }

    // ===================== SaDisGroupItem =====================

    [Fact]
    public async Task DisGroupItem_Save_RejectsZeroBand()
    {
        var sut = CreateSut();

        var result = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 0m, qtyTo: 0m), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("greater than zero", result.ValidationErrors["QtyFr"]);
    }

    [Fact]
    public async Task DisGroupItem_Save_RejectsQtyToBelowQtyFr()
    {
        var sut = CreateSut();

        var result = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 10m, qtyTo: 5m), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("greater than or equal", result.ValidationErrors["QtyTo"]);
    }

    [Fact]
    public async Task DisGroupItem_Save_RejectsOverlappingRule()
    {
        var sut = CreateSut();
        var first = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m), isNew: true);
        Assert.True(first.Succeeded, first.Message);

        var overlap = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 5m, qtyTo: 20m), isNew: true);

        Assert.False(overlap.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, overlap.ErrorCode);
        Assert.Contains("overlaps", overlap.Message);
    }

    [Fact]
    public async Task DisGroupItem_Save_AllowsAdjacentBand()
    {
        var sut = CreateSut();
        var first = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m), isNew: true);
        Assert.True(first.Succeeded, first.Message);

        var adjacent = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 11m, qtyTo: 20m), isNew: true);

        Assert.True(adjacent.Succeeded, adjacent.Message);
    }

    [Fact]
    public async Task DisGroupItem_Save_RejectsPercentageOverHundred()
    {
        var sut = CreateSut();

        var result = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m, discount: 150m), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot exceed", result.ValidationErrors["Discount"]);
    }

    [Fact]
    public async Task DisGroupItem_Save_RejectsBadEffectPrice()
    {
        var sut = CreateSut();
        var rule = BuildRule(qtyFr: 1m, qtyTo: 10m);
        rule.EffectPrice = "WHOLESALE";

        var result = await sut.SaveDisGroupItemAsync(rule, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Contains("DEALER or SELLING", result.ValidationErrors["EffectPrice"]);
    }

    [Fact]
    public async Task DisGroupItem_Save_StampsTenant_AndWritesGroupStatus()
    {
        var sut = CreateSut();

        var result = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m), isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaDisGroupItems.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "I1");
        Assert.Equal("NEW", row.GroupStatus);
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
        Assert.Equal("SELLING", row.EffectPrice);
    }

    [Fact]
    public async Task DisGroupItem_Save_NewUsingDifferentClass_IsAllowedAlongsideBlankClass()
    {
        var sut = CreateSut();
        var blankClass = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m, iClass: null), isNew: true);
        Assert.True(blankClass.Succeeded, blankClass.Message);

        // A blank class applies to all classes, so a class-specific rule in the same band must be rejected.
        var classSpecific = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m, iClass: "C1"), isNew: true);
        Assert.False(classSpecific.Succeeded);
        Assert.Contains("overlaps", classSpecific.Message);
    }

    // ===================== tenant isolation =====================

    [Fact]
    public async Task ItemFamily_IsCompanyScoped()
    {
        var demo = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();
        await SeedItemCustAsync();

        var other = CreateSut(company: "OTHER", canViewPrice: true);

        var groups = await other.ListCustPriceGroupsAsync();
        Assert.True(groups.Succeeded);
        Assert.Empty(groups.Data!);

        var custs = await other.ListItemCustsAsync();
        Assert.True(custs.Succeeded);
        Assert.Empty(custs.Data!);

        var byKey = await other.GetItemCustAsync(new SaItemCustKey
        {
            CustCode = "CUST1",
            ICode = "I1",
            SellingUOM = "PCS",
            MOQ = 0
        });
        Assert.False(byKey.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, byKey.ErrorCode);
    }

    [Fact]
    public async Task ItemFamily_AccessDenied_IsReported()
    {
        var sut = CreateSut(canAccess: false);

        var result = await sut.ListDisGroupItemsAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    // ===================== exports (§11.1, §24) =====================

    [Fact]
    public async Task Export_PriceGroupWorkbook_HasHeadersAndRows()
    {
        var sut = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();

        var result = await sut.ExportCustPriceGroupsAsync();

        Assert.True(result.Succeeded, result.Message);
        var bytes = SaMasterRefExportWorkbooks.BuildCustPriceGroups(result.Data!);
        Assert.Equal(SaMasterRefExportWorkbooks.CustPriceGroupHeaders, SaMasterRefExportWorkbooks.ReadHeaderRow(bytes));

        var row = Assert.Single(SaMasterRefExportWorkbooks.ReadDataRows(bytes));
        Assert.Equal("PL1", row[0]);
        Assert.Equal("1", row[2]);   // one line
        Assert.Equal("Y", row[3]);   // active
    }

    [Fact]
    public async Task Export_CustPrices_WithViewPrice_IncludesThePrice()
    {
        var sut = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();

        var result = await sut.ExportCustPricesAsync("PL1");

        Assert.True(result.Succeeded, result.Message);
        var bytes = SaMasterRefExportWorkbooks.BuildCustPrices("PL1", result.Data!);
        var row = Assert.Single(SaMasterRefExportWorkbooks.ReadDataRows(bytes));
        Assert.Equal("12.5", row[4]);
    }

    [Fact]
    public async Task Export_CustPrices_DeniedViewPrice_LeavesThePriceCellEmpty()
    {
        var denied = CreateSut(canViewPrice: false);
        await SeedPriceGroupAsync();

        var result = await denied.ExportCustPricesAsync("PL1");

        Assert.True(result.Succeeded, result.Message);
        var bytes = SaMasterRefExportWorkbooks.BuildCustPrices("PL1", result.Data!);
        var row = Assert.Single(SaMasterRefExportWorkbooks.ReadDataRows(bytes));

        // Export is not a bypass: without VIEW_PRICE the workbook carries no price.
        Assert.Equal(string.Empty, row[4]);
        Assert.Equal("PCS", row[3]);
    }

    [Fact]
    public async Task Export_WithoutExportPermission_IsDenied()
    {
        var sut = CreateSut(canExport: false);

        var result = await sut.ExportItemCustsAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Export_DisGroupItemWorkbook_WritesDatesAndSlots()
    {
        var sut = CreateSut();
        var saved = await sut.SaveDisGroupItemAsync(BuildRule(qtyFr: 1m, qtyTo: 10m, discount: 7.5m), isNew: true);
        Assert.True(saved.Succeeded, saved.Message);

        var result = await sut.ExportDisGroupItemsAsync();
        Assert.True(result.Succeeded, result.Message);

        var bytes = SaMasterRefExportWorkbooks.BuildDisGroupItems(result.Data!);
        var row = Assert.Single(SaMasterRefExportWorkbooks.ReadDataRows(bytes));
        Assert.Equal("I1", row[0]);
        Assert.Equal("1", row[3]);
        Assert.Equal("10", row[4]);
        Assert.Equal("2026-09-15", row[5]);
        Assert.Equal("7.5", row[7]);
        Assert.Equal("PERCENTAGE", row[8]);
        Assert.Equal("SELLING", row[11]);
    }

    // ===================== SaCust.CustPriceCode assignment (D2-23) =====================

    [Fact]
    public async Task PriceGroupAssignment_BlankAndExistingTolerated_UnknownRejected()
    {
        await SeedPriceGroupAsync();
        var lookups = new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());

        // Clause 1: blank is allowed.
        Assert.True(await lookups.ValidateCustPriceCodeAssignmentAsync(null, null));
        Assert.True(await lookups.ValidateCustPriceCodeAssignmentAsync("   ", null));

        // Clause 2: a non-blank value must exist in the caller's company.
        Assert.True(await lookups.ValidateCustPriceCodeAssignmentAsync("PL1", null));
        Assert.False(await lookups.ValidateCustPriceCodeAssignmentAsync("GONE", null));

        // Clause 3: the value already on the row is tolerated, so legacy free text cannot block an edit.
        Assert.True(await lookups.ValidateCustPriceCodeAssignmentAsync("GONE", "GONE"));
    }

    [Fact]
    public async Task PriceGroupAssignment_RetiredList_NotOfferedButToleratedInPlace()
    {
        await SeedPriceGroupAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL2",
                CustPriceDesc = "RETIRED LIST",
                IsActive = false,
                RowVersion = Rv(41)
            });
            await db.SaveChangesAsync();
        }

        var lookups = new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());
        var options = await lookups.ListPriceGroupsForAssignmentAsync();

        Assert.Contains(options, o => o.Code == "PL1");
        Assert.DoesNotContain(options, o => o.Code == "PL2");   // retired lists are not offered
        Assert.False(await lookups.ValidateCustPriceCodeAssignmentAsync("PL2", null));
        Assert.True(await lookups.ValidateCustPriceCodeAssignmentAsync("PL2", "PL2"));
    }

    // ===================== list scope and bounded payloads (§24 "Paging") =====================

    [Fact]
    public async Task CustPrices_AreScopedToTheirParentPriceGroup_AndToTheCompany()
    {
        var sut = CreateSut(canViewPrice: true);
        await SeedPriceGroupAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL2",
                CustPriceDesc = "PRICE LIST 2",
                IsActive = true,
                RowVersion = Rv(51)
            });
            db.IvCustPrices.Add(new IvCustPrice
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL2",
                ICode = "I2",
                UOM = "PCS",
                SellingPrice = 7m
            });
            await db.SaveChangesAsync();
        }

        var pl1 = await sut.ListCustPricesAsync("PL1");
        Assert.True(pl1.Succeeded, pl1.Message);
        Assert.Equal("I1", Assert.Single(pl1.Data!).ICode);

        var pl2 = await sut.ListCustPricesAsync("PL2");
        Assert.True(pl2.Succeeded, pl2.Message);
        Assert.Equal("I2", Assert.Single(pl2.Data!).ICode);

        // A group belonging to another company is invisible even by its exact code.
        var other = CreateSut(company: "OTHER", canViewPrice: true);
        var foreign = await other.ListCustPricesAsync("PL1");
        Assert.True(foreign.Succeeded, foreign.Message);
        Assert.Empty(foreign.Data!);
    }

    [Fact]
    public async Task PriceGroupList_ReturnsEveryRow_AndTheExportCapIsDocumentedAsGenerous()
    {
        var sut = CreateSut();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var i = 1; i <= 3; i++)
            {
                db.IvCustPriceGroups.Add(new IvCustPriceGroup
                {
                    CompanyCode = "DEMO",
                    CustPriceCode = $"PLB{i}",
                    CustPriceDesc = $"BOUNDED {i}",
                    IsActive = true,
                    RowVersion = Rv(60 + i)
                });
            }

            await db.SaveChangesAsync();
        }

        var list = await sut.ListCustPriceGroupsAsync();
        Assert.True(list.Succeeded, list.Message);
        Assert.Equal(3, list.Data!.Count);   // no accidental small page size on the list surface

        // The list/export surfaces are bounded by a single documented cap rather than paged queries
        // (§14/N-7): the cap must stay far above any realistic master-data volume, so it can never
        // truncate silently in normal use.
        Assert.True(
            SaSalesRefService.MaxExportRows >= 10_000,
            $"MaxExportRows ({SaSalesRefService.MaxExportRows}) is too small to be a safety cap.");
    }

    [Fact]
    public async Task PriceGroupAssignmentOptions_AreCompanyScoped_AndActiveOnly()
    {
        await SeedPriceGroupAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "OTHER",
                CustPriceCode = "PLFOREIGN",
                CustPriceDesc = "FOREIGN LIST",
                IsActive = true,
                RowVersion = Rv(71)
            });
            await db.SaveChangesAsync();
        }

        var lookups = new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());
        var options = await lookups.ListPriceGroupsForAssignmentAsync();

        Assert.Contains(options, o => o.Code == "PL1");
        Assert.DoesNotContain(options, o => o.Code == "PLFOREIGN");
    }

    // ===================== helpers =====================

    private static SaDisGroupItemEditVm BuildRule(
        decimal qtyFr,
        decimal qtyTo,
        string? iClass = null,
        decimal? discount = 10m) =>
        new()
        {
            ICode = "I1",
            IClass = iClass,
            QtyFr = qtyFr,
            QtyTo = qtyTo,
            DateFr = FixedToday,
            Discount = discount,
            DiscountType = discount is null ? null : SaDiscountSlotTypes.Percentage,
            EffectPrice = SaEffectPriceOptions.Selling
        };

    private async Task SeedPriceGroupAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.IvCustPriceGroups.AnyAsync(x => x.CustPriceCode == "PL1"))
        {
            return;
        }

        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL1",
            CustPriceDesc = "PRICE LIST 1",
            IsActive = true,
            RowVersion = Rv(11)
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL1",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 12.50m
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedItemCustAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.SaItemCusts.AnyAsync(x => x.CustCode == "CUST1"))
        {
            return;
        }

        db.SaItemCusts.Add(new SaItemCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST1",
            ICode = "I1",
            SellingUOM = "PCS",
            MOQ = 0,
            CustICode = "CUST-PART-1",
            UnitPrice = 12.34,
            Status = "NEW",
            RowVersion = Rv(31)
        });
        await db.SaveChangesAsync();
    }

    private SaSalesRefService CreateSut(
        string company = "DEMO",
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canViewPrice = false,
        bool canExport = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canExport);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.ViewPrice, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canViewPrice);

        return new SaSalesRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));
    }

    private static byte[] Rv(int seed) => [0, 0, 0, 0, 0, 0, 0, (byte)seed];
}
