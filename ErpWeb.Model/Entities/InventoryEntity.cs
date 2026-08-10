namespace ErpWeb.Model.Entities;

/// <summary>Base for new inventory-scoped entities (not applied to legacy Company/Role).</summary>
public abstract class InventoryEntity
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public abstract class CompanyScopedEntity : InventoryEntity
{
    public int CompanyId { get; set; }
}

public abstract class SoftDeletableCompanyEntity : CompanyScopedEntity
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}

public abstract class BranchScopedEntity : CompanyScopedEntity
{
    public long BranchId { get; set; }
}

public abstract class SoftDeletableBranchEntity : BranchScopedEntity
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}
