using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("Item");
        InventoryEntityConfig.ConfigureSoftDeleteCompany(builder);
        builder.Property(e => e.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ItemDescription).HasMaxLength(200).IsRequired();
        builder.Property(e => e.IsStockItem).HasDefaultValue(true);
        builder.Property(e => e.IsBatchItem).HasDefaultValue(false);
        builder.Property(e => e.CostingMethod).HasConversion<int>();
        builder.Property(e => e.MinStockQty).HasPrecision(19, 6);
        builder.Property(e => e.MaxStockQty).HasPrecision(19, 6);
        builder.Property(e => e.ReorderQty).HasPrecision(19, 6);
        builder.Property(e => e.TaxCode).HasMaxLength(20);
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.HasIndex(e => new { e.CompanyId, e.ItemCode })
            .IsUnique()
            .HasDatabaseName("UQ_Item_CompanyId_ItemCode");
        builder.HasOne<UOM>()
            .WithMany()
            .HasForeignKey(e => e.BaseUOMId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Item_BaseUOM");
    }
}

public class ItemVariantConfiguration : IEntityTypeConfiguration<ItemVariant>
{
    public void Configure(EntityTypeBuilder<ItemVariant> builder)
    {
        builder.ToTable("ItemVariant");
        InventoryEntityConfig.ConfigureSoftDeleteCompany(builder);
        builder.Property(e => e.SKU).HasMaxLength(40).IsRequired();
        builder.Property(e => e.Barcode).HasMaxLength(50);
        builder.Property(e => e.VariantDescription).HasMaxLength(200);
        builder.Property(e => e.IsDefault).HasDefaultValue(false);
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.HasIndex(e => new { e.CompanyId, e.SKU })
            .IsUnique()
            .HasDatabaseName("UQ_ItemVariant_CompanyId_SKU");
        builder.HasOne<Item>()
            .WithMany()
            .HasForeignKey(e => e.ItemId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ItemVariant_Item");
    }
}

public class UOMConfiguration : IEntityTypeConfiguration<UOM>
{
    public void Configure(EntityTypeBuilder<UOM> builder)
    {
        builder.ToTable("UOM");
        InventoryEntityConfig.ConfigureSoftDeleteCompany(builder);
        builder.Property(e => e.UOMCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.UOMName).HasMaxLength(50).IsRequired();
        builder.Property(e => e.DecimalPlaces).HasDefaultValue(4);
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.HasIndex(e => new { e.CompanyId, e.UOMCode })
            .IsUnique()
            .HasDatabaseName("UQ_UOM_CompanyId_UOMCode");
    }
}

public class UOMConversionConfiguration : IEntityTypeConfiguration<UOMConversion>
{
    public void Configure(EntityTypeBuilder<UOMConversion> builder)
    {
        builder.ToTable("UOMConversion");
        InventoryEntityConfig.ConfigureSoftDeleteCompany(builder);
        builder.Property(e => e.ConversionRate).HasPrecision(19, 10);
        builder.HasIndex(e => new { e.CompanyId, e.ItemId, e.FromUOMId, e.ToUOMId })
            .IsUnique()
            .HasDatabaseName("UQ_UOMConversion_Item_From_To");
        builder.HasOne<Item>()
            .WithMany()
            .HasForeignKey(e => e.ItemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UOM>()
            .WithMany()
            .HasForeignKey(e => e.FromUOMId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_UOMConversion_FromUOM");
        builder.HasOne<UOM>()
            .WithMany()
            .HasForeignKey(e => e.ToUOMId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_UOMConversion_ToUOM");
    }
}

public class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("Warehouse");
        InventoryEntityConfig.ConfigureSoftDeleteBranch(builder);
        builder.Property(e => e.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WarehouseName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.WarehouseCode })
            .IsUnique()
            .HasDatabaseName("UQ_Warehouse_Company_Branch_Code");
    }
}

public class WarehouseLocationConfiguration : IEntityTypeConfiguration<WarehouseLocation>
{
    public void Configure(EntityTypeBuilder<WarehouseLocation> builder)
    {
        builder.ToTable("WarehouseLocation");
        InventoryEntityConfig.ConfigureSoftDeleteBranch(builder);
        builder.Property(e => e.LocationCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.LocationName).HasMaxLength(100);
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.WarehouseId, e.LocationCode })
            .IsUnique()
            .HasDatabaseName("UQ_WarehouseLocation_WH_Code");
        builder.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(e => e.WarehouseId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_WarehouseLocation_Warehouse");
    }
}

public class ReasonCodeConfiguration : IEntityTypeConfiguration<ReasonCode>
{
    public void Configure(EntityTypeBuilder<ReasonCode> builder)
    {
        builder.ToTable("ReasonCode");
        InventoryEntityConfig.ConfigureSoftDeleteCompany(builder);
        builder.Property(e => e.ReasonCodeValue).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ReasonName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.AppliesTo).HasMaxLength(50).IsRequired();
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.HasIndex(e => new { e.CompanyId, e.ReasonCodeValue })
            .IsUnique()
            .HasDatabaseName("UQ_ReasonCode_Company_Code");
    }
}
