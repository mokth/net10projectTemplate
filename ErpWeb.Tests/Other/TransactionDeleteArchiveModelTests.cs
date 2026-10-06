using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Other;

[Trait(TestCategories.Name, TestCategories.Shared)]
public sealed class TransactionDeleteArchiveModelTests
{
    [Fact]
    public void Archive_columns_match_module_audit_widths_and_are_not_query_filters()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=ArchiveModelOnly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new AppDbContext(options);

        AssertArchive(db, typeof(IvTrxBatch), deletedByLength: 10);
        AssertArchive(db, typeof(SaDo), deletedByLength: 20);
        AssertArchive(db, typeof(SaInvoice), deletedByLength: 20);
        AssertArchive(db, typeof(SaCdn), deletedByLength: 20);
        AssertArchive(db, typeof(PoInvoice), deletedByLength: 20);
        AssertArchive(db, typeof(PoCdn), deletedByLength: 20);
        AssertArchive(db, typeof(ProductionOutput), deletedByLength: 10);
        AssertArchive(db, typeof(ProductionFinishedGoodReceipt), deletedByLength: 10);

        foreach (var type in new[]
                 {
                     typeof(IvTrxBatch), typeof(SaDo), typeof(SaInvoice), typeof(SaCdn),
                     typeof(PoInvoice), typeof(PoCdn), typeof(ProductionOutput), typeof(ProductionFinishedGoodReceipt)
                 })
        {
            var entity = db.Model.FindEntityType(type)!;
            Assert.DoesNotContain(entity.GetDeclaredQueryFilters(), filter =>
                filter.Expression.ToString()?.Contains("DeletedAtUtc", StringComparison.Ordinal) == true);
        }
    }

    private static void AssertArchive(AppDbContext db, Type clrType, int deletedByLength)
    {
        var entity = db.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"{clrType.Name} is not mapped.");
        var deletedAt = entity.FindProperty("DeletedAtUtc")!;
        var deletedBy = entity.FindProperty("DeletedBy")!;
        var reason = entity.FindProperty("DeleteReason")!;
        Assert.Equal("datetime2", deletedAt.GetColumnType());
        Assert.True(deletedAt.IsNullable);
        Assert.Equal(deletedByLength, deletedBy.GetMaxLength());
        Assert.Equal(250, reason.GetMaxLength());
        Assert.Null(entity.FindProperty("IsDeleted"));
    }
}
