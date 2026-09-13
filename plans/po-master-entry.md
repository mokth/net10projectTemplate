# Purchase Masters — Implementation Plan

> **Goal:** Add master-entry screens for the Purchase master tables, following the project
> standard used by the Sales masters (`SaSalesRepList.razor` / `SaSalesRepList.razor.cs`).
>
> **Tables covered:** `PoBuyer`, `PoBuyingTerm`, `PoCategory`, `PoAuthorised`, `PoPurItem`.

---

## Design decisions (read this first)

| Topic | Decision |
|---|---|
| **Concurrency** | Purchase entities have `RowVersion` (unlike `SaSalesRep`, which uses `SaMasterFingerprint`). Use the **`IvMasterKeyToken` (Code + RowVersion)** pattern, exactly like `PoSupplierService` — **not** the fingerprint pattern. |
| **Service** | One combined service `IPoMasterRefService` / `PoMasterRefService` for all 5 masters — the same shape as `ISaSalesRefService` (one service for all Sales masters). |
| **UI base** | A new `PoRefListPageBase<TRow, TVm>` mirroring `SaRefListPageBase<TRow>`, but row-version based and generic over the edit VM so each page is small. |
| **Tenant scope** | Reads: `TryCompanyScope()` (company-wide, like `SaSalesRefService`). Writes: `TryWriteScope()` (company + branch + location). Swap to `TryBranchScope()` if branch-scoped reads are preferred (like `PoSupplierService`). |
| **Code casing** | Trim-only / case-preserving (matches `PoSupplierService`). Sales masters uppercase — change `NormalizeCode` if that is preferred. |
| **PoPurItem** | PK is `Id` and it has **no `IsActive`** → `SupportsActivate = false`, key token uses `Id.ToString()`. |
| **Deletion** | No reference tables are modelled, so `CanDelete*` returns `Ok()` after the permission check (same "phase-1" stance as supplier refs). Add reference counts later if needed. |

---

## File checklist

| # | Layer | File | Action |
|---|---|---|---|
| 1 | Core | `ErpWeb.Core/Purchase/IPoMasterRefService.cs` | **New** — interface + models |
| 2 | Core | `ErpWeb.Core/Purchase/PoMasterRefService.cs` | **New** — implementation |
| 3 | Core | `ErpWeb.Core/Menus/MenuCodes.cs` | **Edit** — add 5 codes |
| 4 | Core | `ErpWeb.Core/CoreServiceCollectionExtensions.cs` | **Edit** — register service |
| 5 | UI | `ErpWeb.UI/Purchase/Masters/PoRefListPageBase.cs` | **New** — shared base |
| 6 | UI | `PoBuyerList.razor` / `.razor.cs` | **New** |
| 7 | UI | `PoBuyingTermList.razor` / `.razor.cs` | **New** |
| 8 | UI | `PoCategoryList.razor` / `.razor.cs` | **New** |
| 9 | UI | `PoAuthorisedList.razor` / `.razor.cs` | **New** |
| 10 | UI | `PoPurItemList.razor` / `.razor.cs` | **New** |
| 11 | Host | `ErpWeb/Menus/menus.xml` | **Edit** — add menu entries |
| 12 | Tests | `ErpWeb.Tests/PoMasterRefServiceTests.cs` | **New** |

The Model layer (`entities`, `configurations`, `DbSet`) is **already complete** — nothing to add there.

---

## 1. `ErpWeb.Core/Purchase/IPoMasterRefService.cs`

```csharp
using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public interface IPoMasterRefService
{
    // Buyer
    Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> ListBuyersAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyerEditVm>> GetBuyerAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyerEditVm>> SaveBuyerAsync(PoBuyerEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetBuyerActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteBuyersAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteBuyersAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Buying Term
    Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> ListBuyingTermsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyingTermEditVm>> GetBuyingTermAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoBuyingTermEditVm>> SaveBuyingTermAsync(PoBuyingTermEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetBuyingTermActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteBuyingTermsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteBuyingTermsAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Category
    Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> ListCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoCategoryEditVm>> GetCategoryAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoCategoryEditVm>> SaveCategoryAsync(PoCategoryEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetCategoryActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCategoriesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCategoriesAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Authorised person
    Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> ListAuthorisedAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoAuthorisedEditVm>> GetAuthorisedAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoAuthorisedEditVm>> SaveAuthorisedAsync(PoAuthorisedEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetAuthorisedActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteAuthorisedAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteAuthorisedAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Purchase item (PK = Id, no IsActive)
    Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ListPurItemsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoPurItemEditVm>> GetPurItemAsync(string id, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<PoPurItemEditVm>> SavePurItemAsync(PoPurItemEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeletePurItemsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeletePurItemsAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);
}

// ----- Buyer -----
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

// ----- Buying Term -----
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

// ----- Category -----
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

// ----- Authorised -----
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

// ----- Purchase Item -----
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
```

---

## 2. `ErpWeb.Core/Purchase/PoMasterRefService.cs`

The four code masters share one row-version core; `PoPurItem` is handled explicitly because its key is `Id`.

```csharp
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class PoMasterRefService : IPoMasterRefService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;

    public PoMasterRefService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
    }

    // ================= Buyer =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> ListBuyersAsync(CancellationToken ct = default) =>
        ListCoreAsync<PoBuyer, PoBuyerListRow>(MenuCodes.PurchaseBuyer, q => q.OrderBy(x => x.BuyerCode), MapBuyerRow, ct);

    public Task<IvMasterOperationResult<PoBuyerEditVm>> GetBuyerAsync(string code, CancellationToken ct = default) =>
        GetCoreAsync<PoBuyer, PoBuyerEditVm>(MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), code, MapBuyer, ct);

    public Task<IvMasterOperationResult<PoBuyerEditVm>> SaveBuyerAsync(PoBuyerEditVm model, bool isNew, CancellationToken ct = default)
    {
        if (model is null) return Task.FromResult(FailVm<PoBuyerEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Name", model.Name, 100);
        ValidateOptionalLength(errors, "Desc", model.Desc, 200);
        return SaveCoreAsync<PoBuyer, PoBuyerEditVm>(
            MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.BuyerName = Null(v.Name);
                e.BuyerDesc = Null(v.Desc);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoBuyer
            {
                CompanyCode = company, BuyerCode = code, BranchCode = branch, LocationCode = location,
                CreatedDate = now, CreatedBy = user, ModifiedDate = now, ModifiedBy = user
            },
            MapBuyer, ct);
    }

    public Task<IvMasterOperationResult<object>> SetBuyerActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken ct = default) =>
        SetActiveCoreAsync<PoBuyer>(MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), items, isActive,
            e => e.RowVersion, static (e, active, now, user) => { e.IsActive = active; e.ModifiedDate = now; e.ModifiedBy = user; }, ct);

    public Task<DeleteCheckResult> CanDeleteBuyersAsync(IReadOnlyList<string> codes, CancellationToken ct = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseBuyer, codes, ct);

    public Task<IvMasterOperationResult<object>> DeleteBuyersAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken ct = default) =>
        DeleteCoreAsync<PoBuyer>(MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), items, e => e.RowVersion, ct);

    // ================= Buying Term =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> ListBuyingTermsAsync(CancellationToken ct = default) =>
        ListCoreAsync<PoBuyingTerm, PoBuyingTermListRow>(MenuCodes.PurchaseBuyingTerm, q => q.OrderBy(x => x.BuyingTerm), MapBuyingTermRow, ct);

    public Task<IvMasterOperationResult<PoBuyingTermEditVm>> GetBuyingTermAsync(string code, CancellationToken ct = default) =>
        GetCoreAsync<PoBuyingTerm, PoBuyingTermEditVm>(MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), code, MapBuyingTerm, ct);

    public Task<IvMasterOperationResult<PoBuyingTermEditVm>> SaveBuyingTermAsync(PoBuyingTermEditVm model, bool isNew, CancellationToken ct = default)
    {
        if (model is null) return Task.FromResult(FailVm<PoBuyingTermEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Description", model.Description, 200);
        return SaveCoreAsync<PoBuyingTerm, PoBuyingTermEditVm>(
            MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.Description = Null(v.Description);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoBuyingTerm
            {
                CompanyCode = company, BuyingTerm = code, BranchCode = branch, LocationCode = location,
                CreatedDate = now, CreatedBy = user, ModifiedDate = now, ModifiedBy = user
            },
            MapBuyingTerm, ct);
    }

    public Task<IvMasterOperationResult<object>> SetBuyingTermActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken ct = default) =>
        SetActiveCoreAsync<PoBuyingTerm>(MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), items, isActive,
            e => e.RowVersion, static (e, active, now, user) => { e.IsActive = active; e.ModifiedDate = now; e.ModifiedBy = user; }, ct);

    public Task<DeleteCheckResult> CanDeleteBuyingTermsAsync(IReadOnlyList<string> codes, CancellationToken ct = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseBuyingTerm, codes, ct);

    public Task<IvMasterOperationResult<object>> DeleteBuyingTermsAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken ct = default) =>
        DeleteCoreAsync<PoBuyingTerm>(MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), items, e => e.RowVersion, ct);

    // ================= Category =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> ListCategoriesAsync(CancellationToken ct = default) =>
        ListCoreAsync<PoCategory, PoCategoryListRow>(MenuCodes.PurchaseCategory, q => q.OrderBy(x => x.Category), MapCategoryRow, ct);

    public Task<IvMasterOperationResult<PoCategoryEditVm>> GetCategoryAsync(string code, CancellationToken ct = default) =>
        GetCoreAsync<PoCategory, PoCategoryEditVm>(MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), code, MapCategory, ct);

    public Task<IvMasterOperationResult<PoCategoryEditVm>> SaveCategoryAsync(PoCategoryEditVm model, bool isNew, CancellationToken ct = default)
    {
        if (model is null) return Task.FromResult(FailVm<PoCategoryEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Description", model.Description, 200);
        return SaveCoreAsync<PoCategory, PoCategoryEditVm>(
            MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.Description = Null(v.Description);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoCategory
            {
                CompanyCode = company, Category = code, BranchCode = branch, LocationCode = location,
                CreatedDate = now, CreatedBy = user, ModifiedDate = now, ModifiedBy = user
            },
            MapCategory, ct);
    }

    public Task<IvMasterOperationResult<object>> SetCategoryActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken ct = default) =>
        SetActiveCoreAsync<PoCategory>(MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), items, isActive,
            e => e.RowVersion, static (e, active, now, user) => { e.IsActive = active; e.ModifiedDate = now; e.ModifiedBy = user; }, ct);

    public Task<DeleteCheckResult> CanDeleteCategoriesAsync(IReadOnlyList<string> codes, CancellationToken ct = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseCategory, codes, ct);

    public Task<IvMasterOperationResult<object>> DeleteCategoriesAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken ct = default) =>
        DeleteCoreAsync<PoCategory>(MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), items, e => e.RowVersion, ct);

    // ================= Authorised =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> ListAuthorisedAsync(CancellationToken ct = default) =>
        ListCoreAsync<PoAuthorised, PoAuthorisedListRow>(MenuCodes.PurchaseAuthorised, q => q.OrderBy(x => x.Authorised), MapAuthorisedRow, ct);

    public Task<IvMasterOperationResult<PoAuthorisedEditVm>> GetAuthorisedAsync(string code, CancellationToken ct = default) =>
        GetCoreAsync<PoAuthorised, PoAuthorisedEditVm>(MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), code, MapAuthorised, ct);

    public Task<IvMasterOperationResult<PoAuthorisedEditVm>> SaveAuthorisedAsync(PoAuthorisedEditVm model, bool isNew, CancellationToken ct = default)
    {
        if (model is null) return Task.FromResult(FailVm<PoAuthorisedEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Name", model.Name, 100);
        ValidateOptionalLength(errors, "Email", model.Email, 100);
        ValidateOptionalLength(errors, "MobileNo", model.MobileNo, 50);
        if (!string.IsNullOrWhiteSpace(model.Email) && !model.Email.Contains('@'))
        {
            errors["Email"] = "Email is not valid.";
        }

        return SaveCoreAsync<PoAuthorised, PoAuthorisedEditVm>(
            MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.Name = Null(v.Name);
                e.Email = Null(v.Email);
                e.MobileNo = Null(v.MobileNo);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoAuthorised
            {
                CompanyCode = company, Authorised = code, BranchCode = branch, LocationCode = location,
                CreatedDate = now, CreatedBy = user, ModifiedDate = now, ModifiedBy = user
            },
            MapAuthorised, ct);
    }

    public Task<IvMasterOperationResult<object>> SetAuthorisedActiveAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken ct = default) =>
        SetActiveCoreAsync<PoAuthorised>(MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), items, isActive,
            e => e.RowVersion, static (e, active, now, user) => { e.IsActive = active; e.ModifiedDate = now; e.ModifiedBy = user; }, ct);

    public Task<DeleteCheckResult> CanDeleteAuthorisedAsync(IReadOnlyList<string> codes, CancellationToken ct = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseAuthorised, codes, ct);

    public Task<IvMasterOperationResult<object>> DeleteAuthorisedAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken ct = default) =>
        DeleteCoreAsync<PoAuthorised>(MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), items, e => e.RowVersion, ct);

    // ================= Purchase Item (PK = Id) =================
    public async Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ListPurItemsAsync(CancellationToken ct = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Access, ct);
        if (ctx.ErrorCode is not null) return FailList<PoPurItemListRow>(ctx.ErrorCode.Value, ctx.Error);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PoPurItems.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.ICode).ThenBy(x => x.Vendor)
            .ToListAsync(ct);
        return IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>.Ok(rows.Select(MapPurItemRow).ToList());
    }

    public async Task<IvMasterOperationResult<PoPurItemEditVm>> GetPurItemAsync(string id, CancellationToken ct = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Access, ct);
        if (ctx.ErrorCode is not null) return FailVm<PoPurItemEditVm>(ctx.ErrorCode.Value, ctx.Error);

        if (!int.TryParse(NormalizeCode(id), out var key))
        {
            return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Validation, "Purchase item id is required.", "Id");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PoPurItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == ctx.CompanyCode && x.Id == key, ct);
        return entity is null
            ? FailVm<PoPurItemEditVm>(IvMasterErrorCode.NotFound, "Purchase item not found.")
            : IvMasterOperationResult<PoPurItemEditVm>.Ok(MapPurItem(entity));
    }

    public async Task<IvMasterOperationResult<PoPurItemEditVm>> SavePurItemAsync(PoPurItemEditVm model, bool isNew, CancellationToken ct = default)
    {
        if (model is null) return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Validation, "Save request is required.");

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, permission, ct);
        if (ctx.ErrorCode is not null) return FailVm<PoPurItemEditVm>(ctx.ErrorCode.Value, ctx.Error);

        var errors = ValidatePurItem(model);
        if (!isNew && model.RowVersion is not { Length: > 0 })
        {
            return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<PoPurItemEditVm>.Fail(IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var scope = _tenant.TryWriteScope();
        if (scope is null) return FailVm<PoPurItemEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");

        var now = _dates.Now;
        var user = Truncate(scope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        try
        {
            if (isNew)
            {
                var entity = new PoPurItem { CompanyCode = scope.CompanyCode!, BranchCode = scope.BranchCode, LocationCode = scope.LocationCode, CreatedDate = now, CreatedBy = user };
                ApplyPurItem(entity, model, now, user);
                db.PoPurItems.Add(entity);
                await db.SaveChangesAsync(ct);
                return IvMasterOperationResult<PoPurItemEditVm>.Ok(MapPurItem(entity));
            }

            var tracked = await db.PoPurItems.FirstOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.Id == model.Id, ct);
            if (tracked is null) return FailVm<PoPurItemEditVm>(IvMasterErrorCode.NotFound, "Purchase item not found.");
            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            db.Entry(tracked).Property(nameof(PoPurItem.RowVersion)).OriginalValue = model.RowVersion!;
            ApplyPurItem(tracked, model, now, user);
            await db.SaveChangesAsync(ct);
            return IvMasterOperationResult<PoPurItemEditVm>.Ok(MapPurItem(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }
    }

    public async Task<DeleteCheckResult> CanDeletePurItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Delete, ct);
        if (ctx.ErrorCode is not null) return DeleteCheckResult.Blocked(MessageFor(ctx.ErrorCode.Value), []);
        if (!NormalizeCodes(ids).Any()) return DeleteCheckResult.Blocked("No records selected.", []);
        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeletePurItemsAsync(IReadOnlyList<IvMasterKeyToken> items, CancellationToken ct = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Delete, ct);
        if (ctx.ErrorCode is not null) return FailObj(ctx.ErrorCode.Value, ctx.Error);
        if (items is null || items.Count == 0) return FailObj(IvMasterErrorCode.Validation, "No records selected.");

        var scope = _tenant.TryCompanyScope();
        if (scope is null) return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        if (!await _accessRights.CanAsync(MenuCodes.PurchasePurItem, PermissionCodes.Delete, ct))
        {
            return FailObj(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in items)
            {
                if (!int.TryParse(NormalizeCode(item.Code), out var key)) continue;
                var entity = await db.PoPurItems.FirstOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.Id == key, ct);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(ct);
                    return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property(nameof(PoPurItem.RowVersion)).OriginalValue = item.RowVersion;
                db.PoPurItems.Remove(entity);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }
    }

    // ================= generic row-version core =================
    private async Task<IvMasterOperationResult<IReadOnlyList<TRow>>> ListCoreAsync<TEntity, TRow>(
        string menuCode,
        Func<IQueryable<TEntity>, IQueryable<TEntity>> order,
        Func<TEntity, TRow> map,
        CancellationToken ct) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Access, ct);
        if (ctx.ErrorCode is not null) return FailList<TRow>(ctx.ErrorCode.Value, ctx.Error);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await order(
                db.Set<TEntity>().AsNoTracking()
                    .Where(e => EF.Property<string>(e, "CompanyCode") == ctx.CompanyCode))
            .ToListAsync(ct);
        return IvMasterOperationResult<IReadOnlyList<TRow>>.Ok(rows.Select(map).ToList());
    }

    private async Task<IvMasterOperationResult<TVm>> GetCoreAsync<TEntity, TVm>(
        string menuCode, string entityLabel, string codeColumn, string code,
        Func<TEntity, TVm> map, CancellationToken ct) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Access, ct);
        if (ctx.ErrorCode is not null) return FailVm<TVm>(ctx.ErrorCode.Value, ctx.Error);

        var normalized = NormalizeCode(code);
        if (normalized.Length == 0) return FailVm<TVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(
            e => EF.Property<string>(e, "CompanyCode") == ctx.CompanyCode
              && EF.Property<string>(e, codeColumn) == normalized, ct);
        return entity is null
            ? FailVm<TVm>(IvMasterErrorCode.NotFound, $"{entityLabel} not found.")
            : IvMasterOperationResult<TVm>.Ok(map(entity));
    }

    private async Task<IvMasterOperationResult<TVm>> SaveCoreAsync<TEntity, TVm>(
        string menuCode, string entityLabel, string codeColumn, string codeField, int codeMaxLength,
        string code, TVm model, bool isNew, Dictionary<string, string> errors,
        Func<TEntity, byte[]?> rowVersionOf, Func<TVm, byte[]?> vmRowVersionOf,
        Action<TEntity, TVm, DateTime, string> applyFields,
        Func<string, string, string?, string?, DateTime, string, TEntity> create,
        Func<TEntity, TVm> mapVm, CancellationToken ct) where TEntity : class
    {
        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(menuCode, permission, ct);
        if (ctx.ErrorCode is not null) return FailVm<TVm>(ctx.ErrorCode.Value, ctx.Error);

        var normalized = NormalizeCode(code);
        if (normalized.Length == 0) errors[codeField] = "Code is required.";
        else if (normalized.Length > codeMaxLength) errors[codeField] = $"Code must be at most {codeMaxLength} characters.";

        if (!isNew && vmRowVersionOf(model) is not { Length: > 0 })
        {
            return FailVm<TVm>(IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0) return IvMasterOperationResult<TVm>.Fail(IvMasterErrorCode.Validation, "Validation failed.", errors);

        var scope = _tenant.TryWriteScope();
        if (scope is null) return FailVm<TVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");

        var now = _dates.Now;
        var user = Truncate(scope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        try
        {
            if (isNew)
            {
                var exists = await db.Set<TEntity>().AnyAsync(
                    e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                      && EF.Property<string>(e, codeColumn) == normalized, ct);
                if (exists) return FailVm<TVm>(IvMasterErrorCode.DuplicateKey, $"{entityLabel} code already exists.", codeField);

                var entity = create(scope.CompanyCode!, normalized, scope.BranchCode, scope.LocationCode, now, user);
                applyFields(entity, model, now, user);
                db.Set<TEntity>().Add(entity);
                await db.SaveChangesAsync(ct);
                return IvMasterOperationResult<TVm>.Ok(mapVm(entity));
            }

            var tracked = await db.Set<TEntity>().FirstOrDefaultAsync(
                e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                  && EF.Property<string>(e, codeColumn) == normalized, ct);
            if (tracked is null) return FailVm<TVm>(IvMasterErrorCode.NotFound, $"{entityLabel} not found.");
            if (!RowVersionsEqual(rowVersionOf(tracked), vmRowVersionOf(model)))
            {
                return FailVm<TVm>(IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            db.Entry(tracked).Property("RowVersion").OriginalValue = vmRowVersionOf(model)!;
            applyFields(tracked, model, now, user);
            await db.SaveChangesAsync(ct);
            return IvMasterOperationResult<TVm>.Ok(mapVm(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<TVm>(IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<TVm>(IvMasterErrorCode.DuplicateKey, $"{entityLabel} code already exists.", codeField);
        }
    }

    private async Task<IvMasterOperationResult<object>> SetActiveCoreAsync<TEntity>(
        string menuCode, string entityLabel, string codeColumn,
        IReadOnlyList<IvMasterKeyToken> items, bool isActive,
        Func<TEntity, byte[]?> rowVersionOf, Action<TEntity, bool, DateTime, string> setActive,
        CancellationToken ct) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Edit, ct);
        if (ctx.ErrorCode is not null) return FailObj(ctx.ErrorCode.Value, ctx.Error);
        if (items is null || items.Count == 0) return FailObj(IvMasterErrorCode.Validation, "No records selected.");

        var scope = _tenant.TryWriteScope();
        if (scope is null) return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");

        var now = _dates.Now;
        var user = Truncate(scope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in items)
            {
                var code = NormalizeCode(item.Code);
                var entity = await db.Set<TEntity>().FirstOrDefaultAsync(
                    e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                      && EF.Property<string>(e, codeColumn) == code, ct);
                if (entity is null || !RowVersionsEqual(rowVersionOf(entity), item.RowVersion))
                {
                    await tx.RollbackAsync(ct);
                    return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property("RowVersion").OriginalValue = item.RowVersion;
                setActive(entity, isActive, now, user);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }
    }

    private async Task<IvMasterOperationResult<object>> DeleteCoreAsync<TEntity>(
        string menuCode, string entityLabel, string codeColumn,
        IReadOnlyList<IvMasterKeyToken> items, Func<TEntity, byte[]?> rowVersionOf,
        CancellationToken ct) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, ct);
        if (ctx.ErrorCode is not null) return FailObj(ctx.ErrorCode.Value, ctx.Error);
        if (items is null || items.Count == 0) return FailObj(IvMasterErrorCode.Validation, "No records selected.");

        var check = await CanDeleteCoreAsync(menuCode, items.Select(x => x.Code).ToList(), ct);
        if (!check.CanDelete)
        {
            return IvMasterOperationResult<object>.Fail(IvMasterErrorCode.InUse, check.Message ?? "Record is in use.", deleteCheck: check);
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null) return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company context.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in items)
            {
                var code = NormalizeCode(item.Code);
                var entity = await db.Set<TEntity>().FirstOrDefaultAsync(
                    e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                      && EF.Property<string>(e, codeColumn) == code, ct);
                if (entity is null || !RowVersionsEqual(rowVersionOf(entity), item.RowVersion))
                {
                    await tx.RollbackAsync(ct);
                    return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property("RowVersion").OriginalValue = item.RowVersion;
                db.Set<TEntity>().Remove(entity);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }
    }

    private async Task<DeleteCheckResult> CanDeleteCoreAsync(string menuCode, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, ct);
        if (ctx.ErrorCode is not null) return DeleteCheckResult.Blocked(MessageFor(ctx.ErrorCode.Value), []);
        if (!NormalizeCodes(codes).Any()) return DeleteCheckResult.Blocked("No records selected.", []);
        // Phase-1: reference checks are not modelled for these masters. Add counts here later.
        return DeleteCheckResult.Ok();
    }

    // ================= mappers =================
    private static PoBuyerListRow MapBuyerRow(PoBuyer x) => new()
    {
        Code = x.BuyerCode, Name = x.BuyerName, Desc = x.BuyerDesc, IsActive = x.IsActive, RowVersion = x.RowVersion ?? []
    };

    private static PoBuyerEditVm MapBuyer(PoBuyer x) => new()
    {
        Code = x.BuyerCode, Name = x.BuyerName, Desc = x.BuyerDesc, IsActive = x.IsActive,
        RowVersion = x.RowVersion, CreatedDate = x.CreatedDate, CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate, ModifiedBy = x.ModifiedBy
    };

    private static PoBuyingTermListRow MapBuyingTermRow(PoBuyingTerm x) => new()
    {
        Code = x.BuyingTerm, Description = x.Description, IsActive = x.IsActive, RowVersion = x.RowVersion ?? []
    };

    private static PoBuyingTermEditVm MapBuyingTerm(PoBuyingTerm x) => new()
    {
        Code = x.BuyingTerm, Description = x.Description, IsActive = x.IsActive,
        RowVersion = x.RowVersion, CreatedDate = x.CreatedDate, CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate, ModifiedBy = x.ModifiedBy
    };

    private static PoCategoryListRow MapCategoryRow(PoCategory x) => new()
    {
        Code = x.Category, Description = x.Description, IsActive = x.IsActive, RowVersion = x.RowVersion ?? []
    };

    private static PoCategoryEditVm MapCategory(PoCategory x) => new()
    {
        Code = x.Category, Description = x.Description, IsActive = x.IsActive,
        RowVersion = x.RowVersion, CreatedDate = x.CreatedDate, CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate, ModifiedBy = x.ModifiedBy
    };

    private static PoAuthorisedListRow MapAuthorisedRow(PoAuthorised x) => new()
    {
        Code = x.Authorised, Name = x.Name, Email = x.Email, MobileNo = x.MobileNo,
        IsActive = x.IsActive, RowVersion = x.RowVersion ?? []
    };

    private static PoAuthorisedEditVm MapAuthorised(PoAuthorised x) => new()
    {
        Code = x.Authorised, Name = x.Name, Email = x.Email, MobileNo = x.MobileNo, IsActive = x.IsActive,
        RowVersion = x.RowVersion, CreatedDate = x.CreatedDate, CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate, ModifiedBy = x.ModifiedBy
    };

    private static PoPurItemListRow MapPurItemRow(PoPurItem x) => new()
    {
        Id = x.Id, ICode = x.ICode, IDesc = x.IDesc, Category = x.Category, Vendor = x.Vendor,
        VendName = x.VendName, Currency = x.Currency, UnitPrice = x.UnitPrice, Moq = x.Moq,
        Status = x.Status, RowVersion = x.RowVersion ?? []
    };

    private static PoPurItemEditVm MapPurItem(PoPurItem x) => new()
    {
        Id = x.Id, ICode = x.ICode, IDesc = x.IDesc, Category = x.Category, SubCategory = x.SubCategory,
        Dept = x.Dept, PurQty = x.PurQty, PurUom = x.PurUom, Vendor = x.Vendor, VendName = x.VendName,
        VendorPartNo = x.VendorPartNo, Currency = x.Currency, UnitPrice = x.UnitPrice, Moq = x.Moq,
        LeadTime = x.LeadTime, Status = x.Status, Remarks = x.Remarks, PurchaseGlCode = x.PurchaseGlCode,
        RowVersion = x.RowVersion, CreatedDate = x.CreatedDate, CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate, ModifiedBy = x.ModifiedBy
    };

    private static void ApplyPurItem(PoPurItem e, PoPurItemEditVm v, DateTime now, string user)
    {
        e.ICode = (v.ICode ?? string.Empty).Trim();
        e.IDesc = Null(v.IDesc);
        e.Category = Null(v.Category);
        e.SubCategory = Null(v.SubCategory);
        e.Dept = Null(v.Dept);
        e.PurQty = v.PurQty;
        e.PurUom = Null(v.PurUom);
        e.Vendor = (v.Vendor ?? string.Empty).Trim();
        e.VendName = Null(v.VendName);
        e.VendorPartNo = Null(v.VendorPartNo);
        e.Currency = Null(v.Currency);
        e.UnitPrice = v.UnitPrice;
        e.Moq = v.Moq;
        e.LeadTime = v.LeadTime;
        e.Status = Null(v.Status);
        e.Remarks = Null(v.Remarks);
        e.PurchaseGlCode = Null(v.PurchaseGlCode);
        e.ModifiedDate = now;
        e.ModifiedBy = user;
    }

    private static Dictionary<string, string> ValidatePurItem(PoPurItemEditVm v)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var iCode = (v.ICode ?? string.Empty).Trim();
        if (iCode.Length == 0) errors["ICode"] = "Item code is required.";
        else if (iCode.Length > 30) errors["ICode"] = "Item code must be at most 30 characters.";

        var vendor = (v.Vendor ?? string.Empty).Trim();
        if (vendor.Length == 0) errors["Vendor"] = "Vendor is required.";
        else if (vendor.Length > 60) errors["Vendor"] = "Vendor must be at most 60 characters.";

        ValidateOptionalLength(errors, "IDesc", v.IDesc, 200);
        ValidateOptionalLength(errors, "Category", v.Category, 20);
        ValidateOptionalLength(errors, "SubCategory", v.SubCategory, 20);
        ValidateOptionalLength(errors, "Dept", v.Dept, 20);
        ValidateOptionalLength(errors, "PurUom", v.PurUom, 10);
        ValidateOptionalLength(errors, "VendName", v.VendName, 200);
        ValidateOptionalLength(errors, "VendorPartNo", v.VendorPartNo, 50);
        ValidateOptionalLength(errors, "Currency", v.Currency, 20);
        ValidateOptionalLength(errors, "Status", v.Status, 20);
        ValidateOptionalLength(errors, "Remarks", v.Remarks, 250);
        ValidateOptionalLength(errors, "PurchaseGlCode", v.PurchaseGlCode, 20);

        if (v.PurQty < 0) errors["PurQty"] = "Purchase quantity cannot be negative.";
        if (v.Moq < 0) errors["Moq"] = "MOQ cannot be negative.";
        if (v.UnitPrice is < 0) errors["UnitPrice"] = "Unit price cannot be negative.";
        if (v.LeadTime is < 0) errors["LeadTime"] = "Lead time cannot be negative.";
        return errors;
    }

    // ================= helpers =================
    private async Task<ScopeResult> RequireCompanyScopeAsync(string menuCode, string permission, CancellationToken ct)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return ScopeResult.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        if (!await _accessRights.CanAsync(menuCode, permission, ct))
        {
            return ScopeResult.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return ScopeResult.Ok(scope.CompanyCode!, scope.UserId);
    }

    private sealed record ScopeResult(string? CompanyCode, string? UserId, IvMasterErrorCode? ErrorCode, string? Error)
    {
        public static ScopeResult Ok(string companyCode, string? userId) => new(companyCode, userId, null, null);
        public static ScopeResult Fail(IvMasterErrorCode code, string message) => new(null, null, code, message);
    }

    private static string NormalizeCode(string? code) => (code ?? string.Empty).Trim();

    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes) =>
        (codes ?? []).Select(NormalizeCode).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;

    private static void ValidateOptionalLength(Dictionary<string, string> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value.Trim().Length > maxLength) errors[field] = $"{field} must be at most {maxLength} characters.";
    }

    private static string MessageFor(IvMasterErrorCode code) => code switch
    {
        IvMasterErrorCode.AccessDenied => "Not authorized.",
        IvMasterErrorCode.InvalidScope => "Invalid company context.",
        _ => "Request failed."
    };

    private static IvMasterOperationResult<IReadOnlyList<T>> FailList<T>(IvMasterErrorCode code, string? message = null) =>
        IvMasterOperationResult<IReadOnlyList<T>>.Fail(code, message ?? MessageFor(code));

    private static IvMasterOperationResult<T> FailVm<T>(IvMasterErrorCode code, string? message = null, string? field = null)
    {
        IReadOnlyDictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(message))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message };
        }

        return IvMasterOperationResult<T>.Fail(code, message ?? MessageFor(code), errors);
    }

    private static IvMasterOperationResult<object> FailObj(IvMasterErrorCode code, string? message = null) =>
        IvMasterOperationResult<object>.Fail(code, message ?? MessageFor(code));
}
```

---

## 3. `ErpWeb.Core/Menus/MenuCodes.cs` (edit)

Add after `PurchaseSupplierProfile`:

```csharp
    public const string PurchaseBuyer = "PO_BUYER";
    public const string PurchaseBuyingTerm = "PO_BUYING_TERM";
    public const string PurchaseCategory = "PO_CATEGORY";
    public const string PurchaseAuthorised = "PO_AUTHORISED";
    public const string PurchasePurItem = "PO_PUR_ITEM";
```

## 4. `ErpWeb.Core/CoreServiceCollectionExtensions.cs` (edit)

Next to `services.AddScoped<IPoSupplierService, PoSupplierService>();`:

```csharp
        services.AddScoped<IPoMasterRefService, PoMasterRefService>();
```

## 11. `ErpWeb/Menus/menus.xml` (edit)

Inside `<Menu Code="PO_MASTER" ...>`:

```xml
      <Menu Code="PO_SUPPLIER" Name="Supplier" Route="/purchase/suppliers" SortOrder="1" />
      <Menu Code="PO_BUYER" Name="Buyers" Route="/purchase/buyers" SortOrder="2" />
      <Menu Code="PO_BUYING_TERM" Name="Buying Terms" Route="/purchase/buying-terms" SortOrder="3" />
      <Menu Code="PO_CATEGORY" Name="Categories" Route="/purchase/categories" SortOrder="4" />
      <Menu Code="PO_AUTHORISED" Name="Authorised Persons" Route="/purchase/authorised" SortOrder="5" />
      <Menu Code="PO_PUR_ITEM" Name="Purchase Items" Route="/purchase/items" SortOrder="6" />
```

---

## 5. `ErpWeb.UI/Purchase/Masters/PoRefListPageBase.cs`

```csharp
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

/// <summary>
/// Shared list + popup page for Purchase masters keyed by RowVersion.
/// Mirrors the Sales master pages (SaRefListPageBase) with row-version keys.
/// </summary>
public abstract class PoRefListPageBase<TRow, TVm> : PageBase
    where TRow : class
    where TVm : class
{
    [Inject] protected IAccessRightService AccessRights { get; set; } = default!;

    protected DxGrid? Grid;
    protected bool PopupVisible;
    protected bool ConfirmDeleteVisible;
    protected bool IsEditMode;
    protected bool EditEnabled;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;
    protected IEnumerable<TRow>? Data;
    protected readonly List<TRow> SelectedRows = [];
    protected TVm EditModel { get; set; } = default!;
    protected bool CanEditFromView { get; set; }

    protected abstract string MenuCode { get; }
    protected abstract string EntityLabel { get; }
    protected virtual bool SupportsActivate => false;

    protected abstract Task<IvMasterOperationResult<IReadOnlyList<TRow>>> LoadRowsAsync();
    protected abstract TVm CreateNewModel();
    protected abstract Task<IvMasterOperationResult<TVm>> LoadModelAsync(string code);
    protected abstract Task<IvMasterOperationResult<TVm>> SaveModelAsync(TVm model, bool isNew);
    protected abstract string GetRowCode(TRow row);
    protected abstract IvMasterKeyToken ToKeyToken(TRow row);
    protected abstract Task<IvMasterOperationResult<object>> SetActiveCoreAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive);
    protected abstract Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<TRow> rows);
    protected abstract Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items);

    protected List<ButtonInfo> ToolbarButtons
    {
        get
        {
            var buttons = new List<ButtonInfo>
            {
                new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" }
            };

            if (SupportsActivate)
            {
                buttons.Add(new() { IConClass = "fa-solid fa-check", Style = "success", Text = "ACTIVATE", ToolTip = "Activate selected" });
                buttons.Add(new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "DEACTIVATE", ToolTip = "Deactivate selected" });
            }

            buttons.Add(new() { IConClass = "far fa-trash-alt", Style = "danger", Text = "DELETE", ToolTip = "Delete selected" });
            buttons.Add(new() { IConClass = "fa-solid fa-file-excel", Style = "primary", Text = "EXPORT" });
            return buttons;
        }
    }

    protected List<ButtonInfo> RowActionButtons { get; } =
    [
        new() { IConClass = "fa-regular fa-eye", Style = "primary", Text = "VIEW", ToolTip = "View" },
        new() { IConClass = "far fa-edit", Style = "primary", Text = "EDIT", ToolTip = "Edit" }
    ];

    protected string ConfirmDeleteMessage =>
        SelectedRows.Count == 1
            ? $"Delete the selected {EntityLabel}?"
            : $"Delete {SelectedRows.Count} selected {EntityLabel} records?";

    protected override async Task OnPageInitializedAsync()
    {
        EditModel = CreateNewModel();
        await ReloadListAsync();
    }

    protected async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await LoadRowsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? $"Unable to load {EntityLabel} records.";
                Data = [];
            }
            else
            {
                Data = result.Data ?? [];
            }

            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected void OnGridInstance(DxGrid gridInstance) => Grid = gridInstance;

    protected void ClosePopup()
    {
        PopupVisible = false;
        ErrorMessage = null;
        IsSubmitting = false;
    }

    protected void CloseConfirmDelete()
    {
        ConfirmDeleteVisible = false;
        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<TRow> list)
    {
        SelectedRows.Clear();
        if (Data is null) return;
        SelectedRows.AddRange(list.Where(item => Data.Contains(item)));
    }

    protected async Task OnToolbarButtonAsync(SelectedButtonInfo<TRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW": await OnNewClickAsync(); break;
            case "ACTIVATE": await SetActiveBulkAsync(isActive: true); break;
            case "DEACTIVATE": await SetActiveBulkAsync(isActive: false); break;
            case "DELETE": await BeginDeleteAsync(); break;
            case "REFRESH": await ReloadListAsync(); break;
        }
    }

    protected async Task OnRowActionAsync(SelectedButtonInfo<TRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW": await OnViewClickAsync(info.SelectedRow); break;
            case "EDIT": await OnEditClickAsync(info.SelectedRow); break;
        }
    }

    protected async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode)) return true;
        StatusMessage = "Access Denied!!";
        return false;
    }

    protected async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add)) return;
        EditModel = CreateNewModel();
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected async Task OnViewClickAsync(TRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        if (!await LoadEditModelAsync(GetRowCode(row))) return;
        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected async Task OnEditClickAsync(TRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        if (!await LoadEditModelAsync(GetRowCode(row))) return;
        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected async Task SwitchViewToEditAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        EditEnabled = true;
        CanEditFromView = false;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await SaveModelAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? $"{EntityLabel} updated successfully."
                    : $"{EntityLabel} added successfully.";
                await ReloadListAsync();
            }
            else if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ErrorMessage = result.Message ?? "This record was modified by another user.";
                if (IsEditMode) await LoadEditModelAsync(GetRowCode(EditModel));
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task SetActiveBulkAsync(bool isActive)
    {
        if (!SupportsActivate) return;
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        if (SelectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }
        if (IsSubmitting) return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var keys = SelectedRows.Select(ToKeyToken).ToList();
            var result = await SetActiveCoreAsync(keys, isActive);
            if (result.Succeeded)
            {
                StatusMessage = isActive
                    ? $"{EntityLabel} record(s) activated."
                    : $"{EntityLabel} record(s) deactivated.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task BeginDeleteAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Delete)) return;
        if (SelectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }

        var check = await CanDeleteCoreAsync(SelectedRows);
        if (!check.CanDelete)
        {
            ErrorMessage = SaRefListMessages.FormatDeleteBlocked(check);
            StatusMessage = null;
            return;
        }

        ConfirmDeleteVisible = true;
    }

    protected async Task ConfirmDeleteAsync()
    {
        if (IsSubmitting || SelectedRows.Count == 0) return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var keys = SelectedRows.Select(ToKeyToken).ToList();
            var result = await DeleteCoreAsync(keys);
            if (result.Succeeded)
            {
                ConfirmDeleteVisible = false;
                StatusMessage = $"{EntityLabel} record(s) deleted successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
                if (result.DeleteCheck is { CanDelete: false }) ConfirmDeleteVisible = false;
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await LoadModelAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? $"Unable to load {EntityLabel}.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }

    private string GetRowCode(TVm model) =>
        model switch
        {
            PoBuyerEditVm m => m.Code,
            PoBuyingTermEditVm m => m.Code,
            PoCategoryEditVm m => m.Code,
            PoAuthorisedEditVm m => m.Code,
            PoPurItemEditVm m => m.Id.ToString(),
            _ => string.Empty
        };

    protected static IvMasterKeyToken Key(string code, byte[]? rowVersion) =>
        new() { Code = code, RowVersion = rowVersion ?? [] };
}
```

> `SaRefListMessages` lives in `ErpWeb.UI/Admin/Master/SaRefListPageBase.cs` and is `internal`; the project already reuses it from other modules (e.g. `AdSmNumListPageBase`), so this is consistent.

---

## 6. `PoBuyerList.razor`

```razor
@page "/purchase/buyers"
@inherits PoRefListPageBase<PoBuyerListRow, PoBuyerEditVm>

<PageTitle>Buyers</PageTitle>

<MenuAuthorize MenuCode="@MenuCodes.PurchaseBuyer">
<div class="iv-page">
    @if (!string.IsNullOrWhiteSpace(StatusMessage))
    {
        <div class="iv-toast iv-toast--ok" role="status">
            <span>@StatusMessage</span>
            <button type="button" class="iv-toast__close" @onclick="DismissStatus" aria-label="Dismiss">×</button>
        </div>
    }
    @if (!string.IsNullOrWhiteSpace(ErrorMessage) && !PopupVisible)
    {
        <div class="iv-toast iv-toast--err" role="alert">
            <span>@ErrorMessage</span>
            <button type="button" class="iv-toast__close" @onclick="DismissError" aria-label="Dismiss">×</button>
        </div>
    }

    <header class="iv-hero">
        <div class="iv-hero__mark" aria-hidden="true"><i class="fa-solid fa-user-tag"></i></div>
        <div>
            <p class="iv-eyebrow">Purchase</p>
            <h1 class="iv-title">Buyers</h1>
        </div>
    </header>

    @if (IsLoading)
    {
        <p>Loading buyers…</p>
    }

    <CommonDataGridEx Columns="@Columns()"
                      Buttons="@ToolbarButtons"
                      ActionButtons="@RowActionButtons"
                      KeyName="@nameof(PoBuyerListRow.Code)"
                      GridKey="po-buyer-list"
                      Title="Buyers"
                      T="PoBuyerListRow"
                      OnGridInstance="@OnGridInstance"
                      GridData="@Data"
                      allowSelect="true"
                      OnSelectionsEventHandle="@OnSelectionsEvent"
                      OnButtonEventHandle="@OnToolbarButtonAsync"
                      OnActionEventHandle="@OnRowActionAsync" />

    <DxPopup HeaderCssClass="code-style"
             HeaderText="@(IsEditMode ? (EditEnabled ? "Edit Buyer" : "View Buyer") : "New Buyer")"
             CloseOnOutsideClick="false"
             Width="60vw"
             CssClass="common-popup"
             @bind-Visible="@PopupVisible">
        <Content>
            <EditForm Model="@EditModel" Context="buyerForm" OnValidSubmit="@HandleValidSubmitAsync">
                <DataAnnotationsValidator />
                <ValidationSummary />
                <DxFormLayout SizeMode="SizeMode.Small">
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Code" ColSpanMd="4">
                        <Template>
                            <DxTextBox @bind-Text="@EditModel.Code" maxlength="20"
                                       ReadOnly="@IsEditMode" InputCssClass="required-field code-style" />
                        </Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Active" ColSpanMd="2">
                        <Template>
                            <DxCheckBox @bind-Checked="@EditModel.IsActive" AllowIndeterminateState="false" ReadOnly="@(!EditEnabled)" />
                        </Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Name" ColSpanMd="12" BeginRow="true">
                        <Template>
                            <DxTextBox @bind-Text="@EditModel.Name" maxlength="100" ReadOnly="@(!EditEnabled)" />
                        </Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Description" ColSpanMd="12" BeginRow="true">
                        <Template>
                            <DxTextBox @bind-Text="@EditModel.Desc" maxlength="200" ReadOnly="@(!EditEnabled)" />
                        </Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" ColSpanMd="12" BeginRow="true">
                        <Template>
                            <div class="iv-popup-actions">
                                @if (EditEnabled)
                                {
                                    <DxButton RenderStyle="ButtonRenderStyle.Primary" Text="@(IsSubmitting ? "Saving…" : "Save")" SubmitFormOnClick="true" Enabled="@(!IsSubmitting)" />
                                    <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Cancel" Click="@ClosePopup" Enabled="@(!IsSubmitting)" />
                                }
                                else
                                {
                                    <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Close" Click="@ClosePopup" />
                                    @if (CanEditFromView)
                                    {
                                        <DxButton RenderStyle="ButtonRenderStyle.Primary" Text="Edit" Click="@SwitchViewToEditAsync" />
                                    }
                                }
                            </div>
                        </Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" ColSpanMd="12">
                        <Template><small class="text-danger">@ErrorMessage</small></Template>
                    </DxFormLayoutItem>
                </DxFormLayout>
            </EditForm>
        </Content>
    </DxPopup>

    <DxPopup HeaderCssClass="code-style" HeaderText="Confirm Delete" CloseOnOutsideClick="false" Width="420px" CssClass="common-popup" @bind-Visible="@ConfirmDeleteVisible">
        <Content>
            <p>@ConfirmDeleteMessage</p>
            <div class="iv-popup-actions">
                <DxButton RenderStyle="ButtonRenderStyle.Danger" Text="@(IsSubmitting ? "Deleting…" : "Delete")" Click="@ConfirmDeleteAsync" Enabled="@(!IsSubmitting)" />
                <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Cancel" Click="@CloseConfirmDelete" Enabled="@(!IsSubmitting)" />
            </div>
        </Content>
    </DxPopup>
</div>
</MenuAuthorize>
```

## 6b. `PoBuyerList.razor.cs`

```csharp
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoBuyerList : PoRefListPageBase<PoBuyerListRow, PoBuyerEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseBuyer;
    protected override string EntityLabel => "Buyer";
    protected override bool SupportsActivate => true;

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoBuyerListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "100px" },
        new() { Caption = "Name", FieldName = nameof(PoBuyerListRow.Name), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Description", FieldName = nameof(PoBuyerListRow.Desc), DataType = "string", VisibleIndex = 3 },
        new() { Caption = "Active", FieldName = nameof(PoBuyerListRow.IsActive), DataType = "bool", VisibleIndex = 4, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> LoadRowsAsync() => Masters.ListBuyersAsync();
    protected override PoBuyerEditVm CreateNewModel() => new() { IsActive = true };
    protected override Task<IvMasterOperationResult<PoBuyerEditVm>> LoadModelAsync(string code) => Masters.GetBuyerAsync(code);
    protected override Task<IvMasterOperationResult<PoBuyerEditVm>> SaveModelAsync(PoBuyerEditVm model, bool isNew) => Masters.SaveBuyerAsync(model, isNew);
    protected override string GetRowCode(PoBuyerListRow row) => row.Code;
    protected override IvMasterKeyToken ToKeyToken(PoBuyerListRow row) => Key(row.Code, row.RowVersion);
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive) => Masters.SetBuyerActiveAsync(items, isActive);
    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoBuyerListRow> rows) => Masters.CanDeleteBuyersAsync(rows.Select(r => r.Code).ToList());
    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) => Masters.DeleteBuyersAsync(items);
}
```

---

## 7. Buying Term

**`PoBuyingTermList.razor`** — identical to `PoBuyerList.razor` with these replacements:

| Token | Replace with |
|---|---|
| `@page "/purchase/buyers"` | `@page "/purchase/buying-terms"` |
| `PoBuyerListRow, PoBuyerEditVm` | `PoBuyingTermListRow, PoBuyingTermEditVm` |
| `MenuCodes.PurchaseBuyer` | `MenuCodes.PurchaseBuyingTerm` |
| `Buyers` / `Buyer` (titles/hero) | `Buying Terms` / `Buying Term` |
| `GridKey="po-buyer-list"` | `GridKey="po-buying-term-list"` |
| `nameof(PoBuyerListRow.Code)` | `nameof(PoBuyingTermListRow.Code)` |
| `T="PoBuyerListRow"` | `T="PoBuyingTermListRow"` |
| popup field `Description` → `@EditModel.Desc` | `@EditModel.Description` (Caption "Description", `maxlength="200"`) |
| icon `fa-user-tag` | `fa-file-contract` |

**`PoBuyingTermList.razor.cs`**

```csharp
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoBuyingTermList : PoRefListPageBase<PoBuyingTermListRow, PoBuyingTermEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseBuyingTerm;
    protected override string EntityLabel => "Buying term";
    protected override bool SupportsActivate => true;

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoBuyingTermListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PoBuyingTermListRow.Description), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Active", FieldName = nameof(PoBuyingTermListRow.IsActive), DataType = "bool", VisibleIndex = 3, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> LoadRowsAsync() => Masters.ListBuyingTermsAsync();
    protected override PoBuyingTermEditVm CreateNewModel() => new() { IsActive = true };
    protected override Task<IvMasterOperationResult<PoBuyingTermEditVm>> LoadModelAsync(string code) => Masters.GetBuyingTermAsync(code);
    protected override Task<IvMasterOperationResult<PoBuyingTermEditVm>> SaveModelAsync(PoBuyingTermEditVm model, bool isNew) => Masters.SaveBuyingTermAsync(model, isNew);
    protected override string GetRowCode(PoBuyingTermListRow row) => row.Code;
    protected override IvMasterKeyToken ToKeyToken(PoBuyingTermListRow row) => Key(row.Code, row.RowVersion);
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive) => Masters.SetBuyingTermActiveAsync(items, isActive);
    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoBuyingTermListRow> rows) => Masters.CanDeleteBuyingTermsAsync(rows.Select(r => r.Code).ToList());
    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) => Masters.DeleteBuyingTermsAsync(items);
}
```

---

## 8. Category

**`PoCategoryList.razor`** — same template as Buyer with:

| Token | Replace with |
|---|---|
| `@page "/purchase/buyers"` | `@page "/purchase/categories"` |
| `PoBuyerListRow, PoBuyerEditVm` | `PoCategoryListRow, PoCategoryEditVm` |
| `MenuCodes.PurchaseBuyer` | `MenuCodes.PurchaseCategory` |
| titles | `Categories` / `Category` |
| `GridKey` | `po-category-list` |
| popup field | `@EditModel.Description` (Caption "Description", `maxlength="200"`) |
| icon | `fa-folder-tree` |

**`PoCategoryList.razor.cs`**

```csharp
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoCategoryList : PoRefListPageBase<PoCategoryListRow, PoCategoryEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseCategory;
    protected override string EntityLabel => "Category";
    protected override bool SupportsActivate => true;

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoCategoryListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PoCategoryListRow.Description), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Active", FieldName = nameof(PoCategoryListRow.IsActive), DataType = "bool", VisibleIndex = 3, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> LoadRowsAsync() => Masters.ListCategoriesAsync();
    protected override PoCategoryEditVm CreateNewModel() => new() { IsActive = true };
    protected override Task<IvMasterOperationResult<PoCategoryEditVm>> LoadModelAsync(string code) => Masters.GetCategoryAsync(code);
    protected override Task<IvMasterOperationResult<PoCategoryEditVm>> SaveModelAsync(PoCategoryEditVm model, bool isNew) => Masters.SaveCategoryAsync(model, isNew);
    protected override string GetRowCode(PoCategoryListRow row) => row.Code;
    protected override IvMasterKeyToken ToKeyToken(PoCategoryListRow row) => Key(row.Code, row.RowVersion);
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive) => Masters.SetCategoryActiveAsync(items, isActive);
    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoCategoryListRow> rows) => Masters.CanDeleteCategoriesAsync(rows.Select(r => r.Code).ToList());
    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) => Masters.DeleteCategoriesAsync(items);
}
```

---

## 9. Authorised Person

**`PoAuthorisedList.razor`** — same template with:

| Token | Replace with |
|---|---|
| `@page "/purchase/buyers"` | `@page "/purchase/authorised"` |
| `PoBuyerListRow, PoBuyerEditVm` | `PoAuthorisedListRow, PoAuthorisedEditVm` |
| `MenuCodes.PurchaseBuyer` | `MenuCodes.PurchaseAuthorised` |
| titles | `Authorised Persons` / `Authorised Person` |
| `GridKey` | `po-authorised-list` |
| popup fields | `@EditModel.Name` (maxlength 100), `@EditModel.Email` (100), `@EditModel.MobileNo` (50) |
| icon | `fa-user-shield` |

Popup body:

```razor
<DxFormLayout SizeMode="SizeMode.Small">
    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Code" ColSpanMd="4">
        <Template><DxTextBox @bind-Text="@EditModel.Code" maxlength="20" ReadOnly="@IsEditMode" InputCssClass="required-field code-style" /></Template>
    </DxFormLayoutItem>
    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Active" ColSpanMd="2">
        <Template><DxCheckBox @bind-Checked="@EditModel.IsActive" AllowIndeterminateState="false" ReadOnly="@(!EditEnabled)" /></Template>
    </DxFormLayoutItem>
    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Name" ColSpanMd="12" BeginRow="true">
        <Template><DxTextBox @bind-Text="@EditModel.Name" maxlength="100" ReadOnly="@(!EditEnabled)" /></Template>
    </DxFormLayoutItem>
    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Email" ColSpanMd="6" BeginRow="true">
        <Template><DxTextBox @bind-Text="@EditModel.Email" maxlength="100" ReadOnly="@(!EditEnabled)" /></Template>
    </DxFormLayoutItem>
    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Mobile" ColSpanMd="6">
        <Template><DxTextBox @bind-Text="@EditModel.MobileNo" maxlength="50" ReadOnly="@(!EditEnabled)" /></Template>
    </DxFormLayoutItem>
    <DxFormLayoutItem CaptionCssClass="code-style" ColSpanMd="12" BeginRow="true">
        <Template>
            <div class="iv-popup-actions">
                @if (EditEnabled)
                {
                    <DxButton RenderStyle="ButtonRenderStyle.Primary" Text="@(IsSubmitting ? "Saving…" : "Save")" SubmitFormOnClick="true" Enabled="@(!IsSubmitting)" />
                    <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Cancel" Click="@ClosePopup" Enabled="@(!IsSubmitting)" />
                }
                else
                {
                    <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Close" Click="@ClosePopup" />
                    @if (CanEditFromView)
                    {
                        <DxButton RenderStyle="ButtonRenderStyle.Primary" Text="Edit" Click="@SwitchViewToEditAsync" />
                    }
                }
            </div>
        </Template>
    </DxFormLayoutItem>
    <DxFormLayoutItem CaptionCssClass="code-style" ColSpanMd="12">
        <Template><small class="text-danger">@ErrorMessage</small></Template>
    </DxFormLayoutItem>
</DxFormLayout>
```

**`PoAuthorisedList.razor.cs`**

```csharp
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoAuthorisedList : PoRefListPageBase<PoAuthorisedListRow, PoAuthorisedEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseAuthorised;
    protected override string EntityLabel => "Authorised person";
    protected override bool SupportsActivate => true;

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoAuthorisedListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "100px" },
        new() { Caption = "Name", FieldName = nameof(PoAuthorisedListRow.Name), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Email", FieldName = nameof(PoAuthorisedListRow.Email), DataType = "string", VisibleIndex = 3, Width = "180px" },
        new() { Caption = "Mobile", FieldName = nameof(PoAuthorisedListRow.MobileNo), DataType = "string", VisibleIndex = 4, Width = "120px" },
        new() { Caption = "Active", FieldName = nameof(PoAuthorisedListRow.IsActive), DataType = "bool", VisibleIndex = 5, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> LoadRowsAsync() => Masters.ListAuthorisedAsync();
    protected override PoAuthorisedEditVm CreateNewModel() => new() { IsActive = true };
    protected override Task<IvMasterOperationResult<PoAuthorisedEditVm>> LoadModelAsync(string code) => Masters.GetAuthorisedAsync(code);
    protected override Task<IvMasterOperationResult<PoAuthorisedEditVm>> SaveModelAsync(PoAuthorisedEditVm model, bool isNew) => Masters.SaveAuthorisedAsync(model, isNew);
    protected override string GetRowCode(PoAuthorisedListRow row) => row.Code;
    protected override IvMasterKeyToken ToKeyToken(PoAuthorisedListRow row) => Key(row.Code, row.RowVersion);
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive) => Masters.SetAuthorisedActiveAsync(items, isActive);
    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoAuthorisedListRow> rows) => Masters.CanDeleteAuthorisedAsync(rows.Select(r => r.Code).ToList());
    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) => Masters.DeleteAuthorisedAsync(items);
}
```

---

## 10. Purchase Item (no Active, keyed by `Id`)

**`PoPurItemList.razor`**

```razor
@page "/purchase/items"
@inherits PoRefListPageBase<PoPurItemListRow, PoPurItemEditVm>

<PageTitle>Purchase Items</PageTitle>

<MenuAuthorize MenuCode="@MenuCodes.PurchasePurItem">
<div class="iv-page">
    @if (!string.IsNullOrWhiteSpace(StatusMessage))
    {
        <div class="iv-toast iv-toast--ok" role="status">
            <span>@StatusMessage</span>
            <button type="button" class="iv-toast__close" @onclick="DismissStatus" aria-label="Dismiss">×</button>
        </div>
    }
    @if (!string.IsNullOrWhiteSpace(ErrorMessage) && !PopupVisible)
    {
        <div class="iv-toast iv-toast--err" role="alert">
            <span>@ErrorMessage</span>
            <button type="button" class="iv-toast__close" @onclick="DismissError" aria-label="Dismiss">×</button>
        </div>
    }

    <header class="iv-hero">
        <div class="iv-hero__mark" aria-hidden="true"><i class="fa-solid fa-boxes-packing"></i></div>
        <div>
            <p class="iv-eyebrow">Purchase</p>
            <h1 class="iv-title">Purchase Items</h1>
        </div>
    </header>

    @if (IsLoading)
    {
        <p>Loading purchase items…</p>
    }

    <CommonDataGridEx Columns="@Columns()"
                      Buttons="@ToolbarButtons"
                      ActionButtons="@RowActionButtons"
                      KeyName="@nameof(PoPurItemListRow.Id)"
                      GridKey="po-pur-item-list"
                      Title="Purchase Items"
                      T="PoPurItemListRow"
                      OnGridInstance="@OnGridInstance"
                      GridData="@Data"
                      allowSelect="true"
                      OnSelectionsEventHandle="@OnSelectionsEvent"
                      OnButtonEventHandle="@OnToolbarButtonAsync"
                      OnActionEventHandle="@OnRowActionAsync" />

    <DxPopup HeaderCssClass="code-style"
             HeaderText="@(IsEditMode ? (EditEnabled ? "Edit Purchase Item" : "View Purchase Item") : "New Purchase Item")"
             CloseOnOutsideClick="false"
             Width="80vw"
             CssClass="common-popup"
             @bind-Visible="@PopupVisible">
        <Content>
            <EditForm Model="@EditModel" Context="purItemForm" OnValidSubmit="@HandleValidSubmitAsync">
                <DataAnnotationsValidator />
                <ValidationSummary />
                <DxFormLayout SizeMode="SizeMode.Small">
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Item Code" ColSpanMd="4">
                        <Template><DxTextBox @bind-Text="@EditModel.ICode" maxlength="30" InputCssClass="required-field code-style" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Vendor" ColSpanMd="4">
                        <Template><DxTextBox @bind-Text="@EditModel.Vendor" maxlength="60" InputCssClass="required-field code-style" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Currency" ColSpanMd="2">
                        <Template><DxTextBox @bind-Text="@EditModel.Currency" maxlength="20" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Unit Price" ColSpanMd="2">
                        <Template><DxSpinEdit @bind-Value="@EditModel.UnitPrice" MinValue="0" ShowSpinButtons="false" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>

                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Description" ColSpanMd="12" BeginRow="true">
                        <Template><DxTextBox @bind-Text="@EditModel.IDesc" maxlength="200" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Vendor Name" ColSpanMd="6" BeginRow="true">
                        <Template><DxTextBox @bind-Text="@EditModel.VendName" maxlength="200" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Vendor Part No" ColSpanMd="6">
                        <Template><DxTextBox @bind-Text="@EditModel.VendorPartNo" maxlength="50" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Category" ColSpanMd="3" BeginRow="true">
                        <Template><DxTextBox @bind-Text="@EditModel.Category" maxlength="20" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Sub Category" ColSpanMd="3">
                        <Template><DxTextBox @bind-Text="@EditModel.SubCategory" maxlength="20" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Dept" ColSpanMd="3">
                        <Template><DxTextBox @bind-Text="@EditModel.Dept" maxlength="20" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Status" ColSpanMd="3">
                        <Template><DxTextBox @bind-Text="@EditModel.Status" maxlength="20" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Pur Qty" ColSpanMd="3" BeginRow="true">
                        <Template><DxSpinEdit @bind-Value="@EditModel.PurQty" MinValue="0" ShowSpinButtons="false" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Pur UOM" ColSpanMd="3">
                        <Template><DxTextBox @bind-Text="@EditModel.PurUom" maxlength="10" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="MOQ" ColSpanMd="3">
                        <Template><DxSpinEdit @bind-Value="@EditModel.Moq" MinValue="0" ShowSpinButtons="false" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Lead Time" ColSpanMd="3">
                        <Template><DxSpinEdit @bind-Value="@EditModel.LeadTime" MinValue="0" ShowSpinButtons="false" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Purchase GL" ColSpanMd="4" BeginRow="true">
                        <Template><DxTextBox @bind-Text="@EditModel.PurchaseGlCode" maxlength="20" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" Caption="Remarks" ColSpanMd="8">
                        <Template><DxTextBox @bind-Text="@EditModel.Remarks" maxlength="250" ReadOnly="@(!EditEnabled)" /></Template>
                    </DxFormLayoutItem>

                    <DxFormLayoutItem CaptionCssClass="code-style" ColSpanMd="12" BeginRow="true">
                        <Template>
                            <div class="iv-popup-actions">
                                @if (EditEnabled)
                                {
                                    <DxButton RenderStyle="ButtonRenderStyle.Primary" Text="@(IsSubmitting ? "Saving…" : "Save")" SubmitFormOnClick="true" Enabled="@(!IsSubmitting)" />
                                    <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Cancel" Click="@ClosePopup" Enabled="@(!IsSubmitting)" />
                                }
                                else
                                {
                                    <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Close" Click="@ClosePopup" />
                                    @if (CanEditFromView)
                                    {
                                        <DxButton RenderStyle="ButtonRenderStyle.Primary" Text="Edit" Click="@SwitchViewToEditAsync" />
                                    }
                                }
                            </div>
                        </Template>
                    </DxFormLayoutItem>
                    <DxFormLayoutItem CaptionCssClass="code-style" ColSpanMd="12">
                        <Template><small class="text-danger">@ErrorMessage</small></Template>
                    </DxFormLayoutItem>
                </DxFormLayout>
            </EditForm>
        </Content>
    </DxPopup>

    <DxPopup HeaderCssClass="code-style" HeaderText="Confirm Delete" CloseOnOutsideClick="false" Width="420px" CssClass="common-popup" @bind-Visible="@ConfirmDeleteVisible">
        <Content>
            <p>@ConfirmDeleteMessage</p>
            <div class="iv-popup-actions">
                <DxButton RenderStyle="ButtonRenderStyle.Danger" Text="@(IsSubmitting ? "Deleting…" : "Delete")" Click="@ConfirmDeleteAsync" Enabled="@(!IsSubmitting)" />
                <DxButton RenderStyle="ButtonRenderStyle.Secondary" Text="Cancel" Click="@CloseConfirmDelete" Enabled="@(!IsSubmitting)" />
            </div>
        </Content>
    </DxPopup>
</div>
</MenuAuthorize>
```

**`PoPurItemList.razor.cs`**

```csharp
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoPurItemList : PoRefListPageBase<PoPurItemListRow, PoPurItemEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchasePurItem;
    protected override string EntityLabel => "Purchase item";
    protected override bool SupportsActivate => false;   // POPurItem has no Active column

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Item Code", FieldName = nameof(PoPurItemListRow.ICode), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PoPurItemListRow.IDesc), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Category", FieldName = nameof(PoPurItemListRow.Category), DataType = "string", VisibleIndex = 3, Width = "100px" },
        new() { Caption = "Vendor", FieldName = nameof(PoPurItemListRow.Vendor), DataType = "string", VisibleIndex = 4, Width = "110px" },
        new() { Caption = "Vendor Name", FieldName = nameof(PoPurItemListRow.VendName), DataType = "string", VisibleIndex = 5 },
        new() { Caption = "Currency", FieldName = nameof(PoPurItemListRow.Currency), DataType = "string", VisibleIndex = 6, Width = "90px" },
        new() { Caption = "Unit Price", FieldName = nameof(PoPurItemListRow.UnitPrice), DataType = "decimal", VisibleIndex = 7, Width = "110px" },
        new() { Caption = "MOQ", FieldName = nameof(PoPurItemListRow.Moq), DataType = "decimal", VisibleIndex = 8, Width = "90px" },
        new() { Caption = "Status", FieldName = nameof(PoPurItemListRow.Status), DataType = "string", VisibleIndex = 9, Width = "90px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> LoadRowsAsync() => Masters.ListPurItemsAsync();
    protected override PoPurItemEditVm CreateNewModel() => new();
    protected override Task<IvMasterOperationResult<PoPurItemEditVm>> LoadModelAsync(string code) => Masters.GetPurItemAsync(code);
    protected override Task<IvMasterOperationResult<PoPurItemEditVm>> SaveModelAsync(PoPurItemEditVm model, bool isNew) => Masters.SavePurItemAsync(model, isNew);
    protected override string GetRowCode(PoPurItemListRow row) => row.Id.ToString();
    protected override IvMasterKeyToken ToKeyToken(PoPurItemListRow row) => Key(row.Id.ToString(), row.RowVersion);
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(IvMasterErrorCode.Validation, "Activate/deactivate is not supported."));
    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoPurItemListRow> rows) => Masters.CanDeletePurItemsAsync(rows.Select(r => r.Id.ToString()).ToList());
    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) => Masters.DeletePurItemsAsync(items);
}
```

---

## 12. `ErpWeb.Tests/PoMasterRefServiceTests.cs`

```csharp
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class PoMasterRefServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 11);
    private static readonly byte[] Rv1 = [1, 2, 3, 4];

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoMasterRefServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PoCategories.Add(new PoCategory
        {
            CompanyCode = "DEMO", Category = "RAW", Description = "Raw material",
            IsActive = true, RowVersion = Rv1
        });
        db.PoBuyers.Add(new PoBuyer
        {
            CompanyCode = "DEMO", BuyerCode = "B1", BuyerName = "Buyer One",
            IsActive = true, RowVersion = Rv1
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Category_List_ReturnsOnlyCurrentCompany()
    {
        var sut = CreateSut();
        var result = await sut.ListCategoriesAsync();
        Assert.True(result.Succeeded);
        Assert.Single(result.Data!);
        Assert.Equal("RAW", result.Data![0].Code);
    }

    [Fact]
    public async Task Category_Create_StampsTenant()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(new PoCategoryEditVm { Code = "fin", Description = "Finished" }, isNew: true);

        Assert.True(result.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PoCategories.SingleAsync(x => x.CompanyCode == "DEMO" && x.Category == "FIN");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task Category_Create_Duplicate_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(new PoCategoryEditVm { Code = "RAW" }, isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
    }

    [Fact]
    public async Task Category_Update_StaleRowVersion_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(
            new PoCategoryEditVm { Code = "RAW", Description = "Changed", RowVersion = [9, 9] }, isNew: false);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task Category_Update_WithToken_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(
            new PoCategoryEditVm { Code = "RAW", Description = "Changed", RowVersion = Rv1 }, isNew: false);
        Assert.True(result.Succeeded);
        Assert.Equal("Changed", result.Data!.Description);
    }

    [Fact]
    public async Task Delete_RequiresPermission()
    {
        var sut = CreateSut(canDelete: false);
        var result = await sut.DeleteCategoriesAsync([new IvMasterKeyToken { Code = "RAW", RowVersion = Rv1 }]);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    private PoMasterRefService CreateSut(
        string company = "DEMO",
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>())).ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Add, It.IsAny<CancellationToken>())).ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Edit, It.IsAny<CancellationToken>())).ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Delete, It.IsAny<CancellationToken>())).ReturnsAsync(canDelete);

        return new PoMasterRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));
    }
}
```

---

## Notes & follow-ups

1. **RowVersion on SQLite tests** — `AppDbContext` sets `ValueGenerated.Never` for SQLite, so seeded rows must carry an explicit `RowVersion`. On SQL Server it is DB-generated; nothing extra is needed in the service.
2. **`_Imports.razor`** — `ErpWeb.UI/Purchase/_Imports.razor` already imports `ErpWeb.Core.Purchase`, `ErpWeb.Core.Menus`, `DevExpress.Blazor`, and `ErpWeb.UI.Purchase.Masters`, so the new pages need no extra `@using` directives.
3. **Reference checks** — `CanDelete*` currently returns `Ok()` (no reference tables modelled). If deletion should be blocked for `PoCategory` used by `PoSupplier.CategoryCode`, `PoBuyingTerm` used by `PoSupplier.BuyingTerm`, or `PoAuthorised` used by approvals, add counters in `CanDeleteCoreAsync` (mirror `PoSupplierService.CountReferencesBulkAsync`). The UI already handles `DeleteCheckResult.Blocked`.
4. **Export** — the `EXPORT` toolbar button is rendered for parity with the Sales pages but is not wired (Sales master pages likewise omit an export handler). Add an endpoint + `NavigateTo` if needed.
5. **Permission seeds** — menus are synced from `menus.xml` by `MenuSyncService`, so the five new routes/permissions are created automatically on next app start; assign them to roles via Admin → Role Permissions.
6. **Optional next scope** — `PoVendor` / `PoVendorByItem` masters (same Purchase folder) can reuse `PoRefListPageBase<TRow, TVm>` and the same service pattern.
