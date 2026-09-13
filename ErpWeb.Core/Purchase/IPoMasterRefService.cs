using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public interface IPoMasterRefService
{
    // Buyer
    Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> ListBuyersAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> ExportBuyersAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyerEditVm>> GetBuyerAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyerEditVm>> SaveBuyerAsync(PoBuyerEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetBuyerActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteBuyersAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteBuyersAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Buying Term
    Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> ListBuyingTermsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> ExportBuyingTermsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyingTermEditVm>> GetBuyingTermAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyingTermEditVm>> SaveBuyingTermAsync(PoBuyingTermEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetBuyingTermActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteBuyingTermsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteBuyingTermsAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Category
    Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> ListCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> ExportCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoCategoryEditVm>> GetCategoryAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoCategoryEditVm>> SaveCategoryAsync(PoCategoryEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetCategoryActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCategoriesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCategoriesAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Authorised person
    Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> ListAuthorisedAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> ExportAuthorisedAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoAuthorisedEditVm>> GetAuthorisedAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoAuthorisedEditVm>> SaveAuthorisedAsync(PoAuthorisedEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetAuthorisedActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteAuthorisedAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteAuthorisedAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Purchase item (PK = Id, no IsActive)
    Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ListPurItemsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ExportPurItemsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoPurItemEditVm>> GetPurItemAsync(string id, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoPurItemEditVm>> SavePurItemAsync(PoPurItemEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeletePurItemsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeletePurItemsAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);
}

public sealed class PoBuyerListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoBuyerEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class PoBuyingTermListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoBuyingTermEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class PoCategoryListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoCategoryEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class PoAuthorisedListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? MobileNo { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoAuthorisedEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? MobileNo { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class PoPurItemListRow
{
    public int Id { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? Category { get; init; }
    public string? Vendor { get; init; }
    public string? VendName { get; init; }
    public string? Currency { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal Moq { get; init; }
    public string? Status { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoPurItemEditVm
{
    public int Id { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? Category { get; set; }
    public string? SubCategory { get; set; }
    public string? Dept { get; set; }
    public decimal PurQty { get; set; }
    public string? PurUom { get; set; }
    public string Vendor { get; set; } = string.Empty;
    public string? VendName { get; set; }
    public string? VendorPartNo { get; set; }
    public string? Currency { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal Moq { get; set; }
    public int? LeadTime { get; set; }
    public string? Status { get; set; }
    public string? Remarks { get; set; }
    public string? PurchaseGlCode { get; set; }
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
