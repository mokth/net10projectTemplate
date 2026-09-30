using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

/// <summary>
/// Option A1 — exclusive owner with a single cascade path (plan §6.4).
/// <para>
/// The row-level check makes machine ownership and operation ownership mutually exclusive, and the
/// delete graph deliberately exposes only one structural path from <c>Operation</c> to
/// <c>Labour</c> (<c>Operation → Machine → Labour CASCADE</c>). The direct
/// <c>Operation → Labour</c> edge is <c>NO ACTION</c> because SQL Server validates cascade paths
/// against the schema structure rather than per row, so two structural paths would be rejected as
/// a multiple-cascade-path error. Services must delete direct operation-level labour explicitly
/// before deleting an operation.
/// </para>
/// </summary>
public sealed class ProductionWorkOrderLabourConfiguration : IEntityTypeConfiguration<ProductionWorkOrderLabour>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderLabour> builder)
    {
        builder.ToTable("PrWorkOrderLabour", table =>
        {
            // Exactly one of (MachineID, OperationID) is non-null.
            table.HasCheckConstraint(
                "CK_PrWorkOrderLabour_ExclusiveOwner",
                "([MachineID] IS NOT NULL AND [OperationID] IS NULL) "
                + "OR ([MachineID] IS NULL AND [OperationID] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_PrWorkOrderLabour_Amount",
                "[Rate] >= 0 AND [PlannedAmount] >= 0");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.MachineId).HasColumnName("MachineID");
        builder.Property(x => x.OperationId).HasColumnName("OperationID");
        builder.Property(x => x.SourceLabourId).HasColumnName("SourceLabourID");
        builder.Property(x => x.LabourCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LabourDescription).HasMaxLength(200);
        ConfigureNullableQty(builder.Property(x => x.PlannedUnits));
        ConfigureNullableQty(builder.Property(x => x.PlannedMinutes));
        builder.Property(x => x.RateBasis).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Rate).HasPrecision(19, 6).HasDefaultValue(0m).ValueGeneratedNever();
        builder.Property(x => x.ContributesToPlan).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(x => x.PlannedAmount).HasPrecision(19, 4).HasDefaultValue(0m).ValueGeneratedNever();
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.OperationId, x.SourceLabourKey })
            .IsUnique()
            .HasFilter("[MachineID] IS NULL AND [SourceLabourKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrWorkOrderLabour_Operation_SourceKey");

        builder.HasIndex(x => new { x.MachineId, x.SourceLabourKey })
            .IsUnique()
            .HasFilter("[MachineID] IS NOT NULL AND [SourceLabourKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrWorkOrderLabour_Machine_SourceKey");

        builder.HasIndex(x => x.OperationId)
            .HasDatabaseName("IX_PrWorkOrderLabour_OperationID");
        builder.HasIndex(x => x.MachineId)
            .HasDatabaseName("IX_PrWorkOrderLabour_MachineID");

        // Machine-owned labour cascades from the machine.
        builder.HasOne(x => x.Machine)
            .WithMany(x => x.Labours)
            .HasForeignKey(x => x.MachineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Direct operation-level labour is NO ACTION: the service deletes it explicitly so only one
        // structural cascade path (via Machine) exists.
        builder.HasOne(x => x.Operation)
            .WithMany(x => x.Labours)
            .HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();

    private static void ConfigureNullableQty(PropertyBuilder<decimal?> property) =>
        property.HasPrecision(18, 4);
}
