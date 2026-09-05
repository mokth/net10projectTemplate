using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
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
    public DbSet<MsRunningNo> MsRunningNos => Set<MsRunningNo>();
    public DbSet<AdSmNum> AdSmNums => Set<AdSmNum>();
    public DbSet<AdSmNumDate> AdSmNumDates => Set<AdSmNumDate>();

    public DbSet<IvClass> IvClasses => Set<IvClass>();
    public DbSet<IvSubClass> IvSubClasses => Set<IvSubClass>();
    public DbSet<IvType> IvTypes => Set<IvType>();
    public DbSet<IvStatus> IvStatuses => Set<IvStatus>();
    public DbSet<IvWarehouse> IvWarehouses => Set<IvWarehouse>();
    public DbSet<IvLocation> IvLocations => Set<IvLocation>();
    public DbSet<MsUom> MsUoms => Set<MsUom>();
    public DbSet<IvStockMaster> IvStockMasters => Set<IvStockMaster>();
    public DbSet<IvLot> IvLots => Set<IvLot>();
    public DbSet<IvBalLoc> IvBalLocs => Set<IvBalLoc>();
    public DbSet<IvTrxBatch> IvTrxBatches => Set<IvTrxBatch>();
    public DbSet<IvTrxBatchDetail> IvTrxBatchDetails => Set<IvTrxBatchDetail>();
    public DbSet<IvTrxHistory> IvTrxHistories => Set<IvTrxHistory>();

    public DbSet<SaCust> SaCusts => Set<SaCust>();
    public DbSet<SaCustAdd> SaCustAdds => Set<SaCustAdd>();
    public DbSet<SaCustContact> SaCustContacts => Set<SaCustContact>();
    public DbSet<SaCustType> SaCustTypes => Set<SaCustType>();
    public DbSet<SaCustGroup> SaCustGroups => Set<SaCustGroup>();
    public DbSet<IvAreaCode> IvAreaCodes => Set<IvAreaCode>();
    public DbSet<IvMsCode> IvMsCodes => Set<IvMsCode>();
    public DbSet<SaCountry> SaCountries => Set<SaCountry>();
    public DbSet<SaCurrency> SaCurrencies => Set<SaCurrency>();
    public DbSet<SaDisGroup> SaDisGroups => Set<SaDisGroup>();
    public DbSet<SaDisCust> SaDisCusts => Set<SaDisCust>();
    public DbSet<SaCurrRate> SaCurrRates => Set<SaCurrRate>();
    public DbSet<SaInvoice> SaInvoices => Set<SaInvoice>();
    public DbSet<SaInvoiceDetail> SaInvoiceDetails => Set<SaInvoiceDetail>();
    public DbSet<SaTaxGroup> SaTaxGroups => Set<SaTaxGroup>();
    public DbSet<SaPaymentTerm> SaPaymentTerms => Set<SaPaymentTerm>();
    public DbSet<SaSalesRep> SaSalesReps => Set<SaSalesRep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // SQL Server rowversion is DB-generated. SQLite (tests) has no equivalent — send CLR value.
        var provider = Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(byte[]) &&
                        string.Equals(property.Name, "RowVersion", StringComparison.Ordinal))
                    {
                        property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                    }
                }
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
