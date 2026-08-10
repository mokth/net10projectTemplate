using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserLogin> UserLogins => Set<UserLogin>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRoleMapping> UserRoleMappings => Set<UserRoleMapping>();
    public DbSet<MenuPermission> MenuPermissions => Set<MenuPermission>();
    public DbSet<RoleMenuPermission> RoleMenuPermissions => Set<RoleMenuPermission>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemVariant> ItemVariants => Set<ItemVariant>();
    public DbSet<UOM> UOMs => Set<UOM>();
    public DbSet<UOMConversion> UOMConversions => Set<UOMConversion>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<WarehouseLocation> WarehouseLocations => Set<WarehouseLocation>();
    public DbSet<ReasonCode> ReasonCodes => Set<ReasonCode>();
    public DbSet<InventoryDocument> InventoryDocuments => Set<InventoryDocument>();
    public DbSet<InventoryDocumentLine> InventoryDocumentLines => Set<InventoryDocumentLine>();
    public DbSet<StockMovementAllocation> StockMovementAllocations => Set<StockMovementAllocation>();
    public DbSet<StockLedger> StockLedgers => Set<StockLedger>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<ItemCost> ItemCosts => Set<ItemCost>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<LedgerSequence> LedgerSequences => Set<LedgerSequence>();
    public DbSet<StockTake> StockTakes => Set<StockTake>();
    public DbSet<StockTakeLine> StockTakeLines => Set<StockTakeLine>();
    public DbSet<InventoryPeriod> InventoryPeriods => Set<InventoryPeriod>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<LotBalance> LotBalances => Set<LotBalance>();
    public DbSet<InventoryDocumentLineLotSplit> InventoryDocumentLineLotSplits => Set<InventoryDocumentLineLotSplit>();
    public DbSet<StockSnapshot> StockSnapshots => Set<StockSnapshot>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampSqliteRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampSqliteRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampSqliteRowVersions()
    {
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<InventoryEntity>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            if (entry.Entity.RowVersion is { Length: > 0 })
            {
                continue;
            }

            entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty(nameof(InventoryEntity.RowVersion));
                if (rowVersion is null)
                {
                    continue;
                }

                rowVersion.SetColumnType("BLOB");
                rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                rowVersion.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Save);
                rowVersion.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}