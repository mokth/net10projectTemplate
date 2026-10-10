using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests.Sales.Delivery;

[Trait(TestCategories.Name, TestCategories.Sales)]
public sealed class SaDeliveryModelContractTests
{
    private static readonly Type[] DeliveryEntityTypes =
    [
        typeof(SaDeliveryDriver),
        typeof(SaDeliveryVehicle),
        typeof(SaDeliveryTrip),
        typeof(SaDeliveryTripStop),
        typeof(SaDeliveryTripStopDo),
        typeof(SaDeliveryAttempt),
        typeof(SaDeliveryAttemptDo),
        typeof(SaDeliveryPodAttachment),
        typeof(SaDeliveryExceptionReason)
    ];

    [Fact]
    public void Delivery_keys_and_relationships_keep_company_and_branch_scope()
    {
        using var db = CreateSqlServerModelContext();
        var model = db.Model;

        foreach (var clrType in DeliveryEntityTypes)
        {
            var entity = Assert.IsAssignableFrom<IEntityType>(model.FindEntityType(clrType));
            var keyNames = entity.FindPrimaryKey()!.Properties.Select(x => x.Name).ToArray();
            Assert.Contains(nameof(SaDeliveryDriver.CompanyCode), keyNames);
            Assert.Contains(nameof(SaDeliveryDriver.BranchCode), keyNames);

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var foreignKeyNames = foreignKey.Properties.Select(x => x.Name).ToArray();
                var principalKeyNames = foreignKey.PrincipalKey.Properties.Select(x => x.Name).ToArray();
                Assert.Contains(nameof(SaDeliveryDriver.CompanyCode), foreignKeyNames);
                Assert.Contains(nameof(SaDeliveryDriver.BranchCode), foreignKeyNames);
                Assert.Equal(foreignKeyNames, principalKeyNames);
                Assert.NotEqual(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
            }

            var rowVersion = entity.FindProperty("RowVersion");
            if (rowVersion is not null)
            {
                Assert.True(rowVersion.IsConcurrencyToken);
                Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
            }
        }
    }

    [Fact]
    public void Active_delivery_order_assignment_is_a_filtered_unique_index()
    {
        using var db = CreateSqlServerModelContext();
        var entity = db.Model.FindEntityType(typeof(SaDeliveryTripStopDo));
        Assert.NotNull(entity);

        var index = Assert.Single(entity!.GetIndexes(), x =>
            x.GetDatabaseName() == "UX_SaDeliveryTripStopDo_ActiveAssignment");

        Assert.True(index.IsUnique);
        Assert.Equal("[IsActiveAssignment] = 1", index.GetFilter());
        Assert.Equal(
            new[] { "CompanyCode", "BranchCode", "DoNo" },
            index.Properties.Select(x => x.Name).ToArray());
    }

    private static AppDbContext CreateSqlServerModelContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=DeliveryModelOnly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new AppDbContext(options);
    }
}
