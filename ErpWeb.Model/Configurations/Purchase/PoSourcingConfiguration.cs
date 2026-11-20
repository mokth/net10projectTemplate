using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public sealed class PoSourcingHdrConfiguration : IEntityTypeConfiguration<PoSourcingHdr>
{
    public void Configure(EntityTypeBuilder<PoSourcingHdr> builder)
    {
        builder.ToTable("PoSourcingHdr");
        builder.HasKey(x => x.SourcingId);
        builder.Property(x => x.SourcingId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.SourcingNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourcePrNo).HasMaxLength(30);
        builder.Property(x => x.Requester).HasMaxLength(50);
        builder.Property(x => x.BuyerCode).HasMaxLength(20);
        builder.Property(x => x.DeptCode).HasMaxLength(20);
        builder.Property(x => x.ProjectCode).HasMaxLength(20);
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30);
        builder.Property(x => x.DeliveryWhse).HasMaxLength(20);
        builder.Property(x => x.RequestDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReplyDeadline).HasColumnType("datetime2");
        builder.Property(x => x.RequiredDate).HasColumnType("datetime2");
        builder.Property(x => x.BaseCurrency).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CancelReason).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SourcingNo })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingHdr_Tenant_SourcingNo");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.RequestDate })
            .HasDatabaseName("IX_PoSourcingHdr_Tenant_Status_Date");

        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Suppliers)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Quotes)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Awards)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Conversions)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Attachments)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Events)
            .WithOne(x => x.Sourcing)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SourcingId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingLineConfiguration : IEntityTypeConfiguration<PoSourcingLine>
{
    public void Configure(EntityTypeBuilder<PoSourcingLine> builder)
    {
        builder.ToTable("PoSourcingLine");
        builder.HasKey(x => x.SourcingLineId);
        builder.Property(x => x.SourcingLineId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ICode).HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Specification).HasMaxLength(500);
        builder.Property(x => x.RequestedUom).HasMaxLength(20).IsRequired();
        builder.Property(x => x.StdUom).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PackSz).HasColumnType("decimal(18,6)");
        builder.Property(x => x.RequestedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.RequestedStdQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.RequiredDate).HasColumnType("datetime2");
        builder.Property(x => x.DeliveryWhse).HasMaxLength(20);
        builder.Property(x => x.PrNo).HasMaxLength(30);
        builder.Property(x => x.PrOriginalVendorCd).HasMaxLength(20);
        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.HasIndex(x => new { x.SourcingId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingLine_Sourcing_LineNo");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PrNo, x.PrLineNo })
            .HasDatabaseName("IX_PoSourcingLine_Tenant_PrLine");
    }
}

public sealed class PoSourcingSupplierConfiguration : IEntityTypeConfiguration<PoSourcingSupplier>
{
    public void Configure(EntityTypeBuilder<PoSourcingSupplier> builder)
    {
        builder.ToTable("PoSourcingSupplier");
        builder.HasKey(x => x.SourcingSupplierId);
        builder.Property(x => x.SourcingSupplierId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.SuppCode).HasMaxLength(20);
        builder.Property(x => x.SupplierName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ContactPerson).HasMaxLength(100);
        builder.Property(x => x.ContactEmail).HasMaxLength(100);
        builder.Property(x => x.ContactPhone).HasMaxLength(50);
        builder.Property(x => x.DefaultCurrency).HasMaxLength(20);
        builder.Property(x => x.ResponseStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();

        builder.HasIndex(x => new { x.SourcingId, x.SupplierName })
            .HasDatabaseName("IX_PoSourcingSupplier_Sourcing_Name");
    }
}

public sealed class PoSourcingQuoteConfiguration : IEntityTypeConfiguration<PoSourcingQuote>
{
    public void Configure(EntityTypeBuilder<PoSourcingQuote> builder)
    {
        builder.ToTable("PoSourcingQuote");
        builder.HasKey(x => x.QuoteId);
        builder.Property(x => x.QuoteId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.QuoteId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.SupplierQuoteRef).HasMaxLength(50).IsRequired();
        builder.Property(x => x.QuoteDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ExpiryDate).HasColumnType("datetime2");
        builder.Property(x => x.Currency).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ExchangeRateToBase).HasColumnType("decimal(18,8)");
        builder.Property(x => x.ExchangeRateDate).HasColumnType("datetime2");
        builder.Property(x => x.PayTermCode).HasMaxLength(20);
        builder.Property(x => x.BuyingTermCode).HasMaxLength(20);
        builder.Property(x => x.QuoteChannel).HasMaxLength(30);
        builder.Property(x => x.HeaderFreightAmount).HasColumnType("decimal(18,2)");
        builder.Property(x => x.HeaderOtherCharges).HasColumnType("decimal(18,2)");
        builder.Property(x => x.FreightAllocationMode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.BuyerNotes).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.SourcingId, x.SourcingSupplierId, x.QuoteRevNo })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingQuote_Supplier_Rev");

        builder.HasOne(x => x.Supplier)
            .WithMany()
            .HasForeignKey(x => x.SourcingSupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Quote)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.QuoteId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.QuoteId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingQuoteLineConfiguration : IEntityTypeConfiguration<PoSourcingQuoteLine>
{
    public void Configure(EntityTypeBuilder<PoSourcingQuoteLine> builder)
    {
        builder.ToTable("PoSourcingQuoteLine");
        builder.HasKey(x => x.QuoteLineId);
        builder.Property(x => x.QuoteLineId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LineStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.VendorPartNo).HasMaxLength(50);
        builder.Property(x => x.OfferedSpec).HasMaxLength(500);
        builder.Property(x => x.QuotedUom).HasMaxLength(20).IsRequired();
        builder.Property(x => x.QuotedPackSz).HasColumnType("decimal(18,6)");
        builder.Property(x => x.QuotedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.QuotedStdQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.UnitPrice).HasColumnType("decimal(18,6)");
        builder.Property(x => x.DiscPer).HasColumnType("decimal(9,4)");
        builder.Property(x => x.DiscAmt).HasColumnType("decimal(18,2)");
        builder.Property(x => x.TaxGroupCode).HasMaxLength(20);
        builder.Property(x => x.TaxPer).HasColumnType("decimal(9,4)");
        builder.Property(x => x.NonRecoverableTaxPer).HasColumnType("decimal(9,4)");
        builder.Property(x => x.LineCharges).HasColumnType("decimal(18,2)");
        builder.Property(x => x.EstimatedDeliveryDate).HasColumnType("datetime2");
        builder.Property(x => x.Moq).HasColumnType("decimal(18,4)");
        builder.Property(x => x.WarrantyTerms).HasMaxLength(200);
        builder.Property(x => x.ExceptionsNote).HasMaxLength(500);
        builder.Property(x => x.NonComparableReason).HasMaxLength(200);
        builder.Property(x => x.NormalizedBaseUnitCost).HasColumnType("decimal(18,6)");
        builder.Property(x => x.NormalizedComparableLandedTotal).HasColumnType("decimal(18,2)");
        builder.Property(x => x.NormalizedTaxInclusivePayableTotal).HasColumnType("decimal(18,2)");

        builder.HasIndex(x => new { x.QuoteId, x.SourcingLineId })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingQuoteLine_Quote_SourcingLine");

        builder.HasOne(x => x.SourcingLine)
            .WithMany()
            .HasForeignKey(x => x.SourcingLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingAwardConfiguration : IEntityTypeConfiguration<PoSourcingAward>
{
    public void Configure(EntityTypeBuilder<PoSourcingAward> builder)
    {
        builder.ToTable("PoSourcingAward");
        builder.HasKey(x => x.AwardId);
        builder.Property(x => x.AwardId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.AwardId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OverallReason).HasMaxLength(500);
        builder.Property(x => x.ExceptionReason).HasMaxLength(500);
        builder.Property(x => x.ProposedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ProposedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.DecidedBy).HasMaxLength(20);
        builder.Property(x => x.DecidedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.DecisionReason).HasMaxLength(500);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.SourcingId, x.AwardVersion })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingAward_Sourcing_Version");

        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Award)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.AwardId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.AwardId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingAwardLineConfiguration : IEntityTypeConfiguration<PoSourcingAwardLine>
{
    public void Configure(EntityTypeBuilder<PoSourcingAwardLine> builder)
    {
        builder.ToTable("PoSourcingAwardLine");
        builder.HasKey(x => x.AwardLineId);
        builder.Property(x => x.AwardLineId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.SuppCodeSnapshot).HasMaxLength(20);
        builder.Property(x => x.SupplierNameSnapshot).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ICodeSnapshot).HasMaxLength(50);
        builder.Property(x => x.CurrencySnapshot).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ExchangeRateSnapshot).HasColumnType("decimal(18,8)");
        builder.Property(x => x.AwardedUom).HasMaxLength(20).IsRequired();
        builder.Property(x => x.StdUomSnapshot).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PackSzSnapshot).HasColumnType("decimal(18,6)");
        builder.Property(x => x.AwardedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.AwardedStdQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.AwardedUnitPrice).HasColumnType("decimal(18,6)");
        builder.Property(x => x.DiscPerSnapshot).HasColumnType("decimal(9,4)");
        builder.Property(x => x.TaxGroupSnapshot).HasMaxLength(20);
        builder.Property(x => x.TaxPerSnapshot).HasColumnType("decimal(9,4)");
        builder.Property(x => x.ComparableBaseUnitCostSnapshot).HasColumnType("decimal(18,6)");
        builder.Property(x => x.ComparableTotalSnapshot).HasColumnType("decimal(18,2)");
        builder.Property(x => x.SelectionReason).HasMaxLength(500);
        builder.Property(x => x.QuarantineReason).HasMaxLength(500);

        builder.HasIndex(x => new { x.AwardId, x.SourcingLineId, x.QuoteLineId })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingAwardLine_Award_Line_Quote");

        builder.HasOne(x => x.SourcingLine)
            .WithMany()
            .HasForeignKey(x => x.SourcingLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingConversionConfiguration : IEntityTypeConfiguration<PoSourcingConversion>
{
    public void Configure(EntityTypeBuilder<PoSourcingConversion> builder)
    {
        builder.ToTable("PoSourcingConversion");
        builder.HasKey(x => x.ConversionId);
        builder.Property(x => x.ConversionId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.ConversionId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.RequestId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.PayloadHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.TargetDocType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TargetDocNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.TargetSupplierCode).HasMaxLength(20);
        builder.Property(x => x.TargetCurrency).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LastReconciledAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.ReconciliationNote).HasMaxLength(500);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.RequestId })
            .IsUnique()
            .HasDatabaseName("UX_PoSourcingConversion_Tenant_RequestId");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TargetDocType, x.TargetDocNo, x.TargetDocRelNo })
            .HasDatabaseName("IX_PoSourcingConversion_Tenant_TargetDoc");

        builder.HasOne(x => x.Award)
            .WithMany()
            .HasForeignKey(x => x.AwardId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Conversion)
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.ConversionId })
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ConversionId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingConversionLineConfiguration : IEntityTypeConfiguration<PoSourcingConversionLine>
{
    public void Configure(EntityTypeBuilder<PoSourcingConversionLine> builder)
    {
        builder.ToTable("PoSourcingConversionLine");
        builder.HasKey(x => x.ConversionLineId);
        builder.Property(x => x.ConversionLineId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TargetDocType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TargetDocNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.TargetUom).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TargetPackSz).HasColumnType("decimal(18,6)");
        builder.Property(x => x.ConvertedPurQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ConvertedStdQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.EffectiveActiveStdQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.TargetUnitPrice).HasColumnType("decimal(18,6)");
        builder.Property(x => x.SourcePrNo).HasMaxLength(30);
        builder.Property(x => x.LineLinkStatus).HasMaxLength(30).IsRequired();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TargetDocType, x.TargetDocNo, x.TargetDocRelNo, x.TargetLineNo })
            .HasDatabaseName("IX_PoSourcingConversionLine_Tenant_TargetLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.AwardLineId })
            .HasDatabaseName("IX_PoSourcingConversionLine_Tenant_AwardLine");

        builder.HasOne(x => x.AwardLine)
            .WithMany()
            .HasForeignKey(x => x.AwardLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PoSourcingAttachmentConfiguration : IEntityTypeConfiguration<PoSourcingAttachment>
{
    public void Configure(EntityTypeBuilder<PoSourcingAttachment> builder)
    {
        builder.ToTable("PoSourcingAttachment");
        builder.HasKey(x => x.AttachmentId);
        builder.Property(x => x.AttachmentId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ScopeKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.StoredRelativePath).HasMaxLength(500).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.UploadedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.UploadedBy).HasMaxLength(20).IsRequired();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SourcingId, x.QuoteId })
            .HasDatabaseName("IX_PoSourcingAttachment_Tenant_Sourcing_Quote");
    }
}

public sealed class PoSourcingEventConfiguration : IEntityTypeConfiguration<PoSourcingEvent>
{
    public void Configure(EntityTypeBuilder<PoSourcingEvent> builder)
    {
        builder.ToTable("PoSourcingEvent");
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PriorStatus).HasMaxLength(30);
        builder.Property(x => x.NewStatus).HasMaxLength(30);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.SnapshotHash).HasMaxLength(128);
        builder.Property(x => x.ActorUserId).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OccurredAtUtc).HasColumnType("datetime2").IsRequired();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SourcingId, x.OccurredAtUtc })
            .HasDatabaseName("IX_PoSourcingEvent_Tenant_Sourcing_Date");
    }
}
