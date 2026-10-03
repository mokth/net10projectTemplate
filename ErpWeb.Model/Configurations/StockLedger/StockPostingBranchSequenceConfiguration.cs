using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockPostingBranchSequenceConfiguration : IEntityTypeConfiguration<StockPostingBranchSequence>
{
    public void Configure(EntityTypeBuilder<StockPostingBranchSequence> builder)
    {
        builder.ToTable("StockPostingBranchSequence", table =>
            table.HasCheckConstraint("CK_StockPostingBranchSequence_Value", "[LastSequence] >= 0"));
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode });
        builder.Property(x => x.CompanyCode).HasMaxLength(5);
        builder.Property(x => x.BranchCode).HasMaxLength(5);
        builder.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
