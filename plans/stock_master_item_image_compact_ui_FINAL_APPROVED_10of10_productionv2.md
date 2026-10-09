# Stock Master Item Image + Professional Compact Entry UI
## FINAL AI Code Agent Implementation Plan — `productionv2`

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified baseline commit:** `a552ed1f39bfaa786e1904c5037390521b052586`  
**Baseline verified again:** 2026-10-09  
**Plan status:** **APPROVED FOR IMPLEMENTATION**  
**Professional review status:** **PASSED**  
**Implementation readiness:** **10/10**  
**Scope:** Stock Master Entry UI redesign + one primary item image + minimum supporting service/storage/read endpoint/tests.  

> This document supersedes the earlier Stock Master item-image plan.  
> The coding agent must implement this final version, not combine it with the older two-step image-persistence flow.

---

# 0. Executive Implementation Decision

Implement a compact, professional Stock Master Entry and one primary item image while preserving the current ERP business rules.

The final architecture is:

```text
Browser / Blazor Stock Master page
        │
        ├── Select image
        │      ↓
        │   IIvStockMasterImageService.PrepareAsync(...)
        │      ↓
        │   validated + auto-oriented + resized + normalized WebP
        │   held only as bounded pending server-side page state
        │
        └── SAVE
               ↓
        IIvStockMasterService.SaveAsync(
            model,
            isNew,
            imageChange)
               ↓
        existing business validation + permission + RowVersion validation
               ↓
        if replacement:
            store new managed file
               ↓
        set business fields + server-controlled ImagePath in ONE DB save
               ↓
        DB success
          ├── delete old image best-effort
          └── return newest RowVersion + HasImage
```

**Critical invariant:**

```text
The browser never supplies ImagePath.
The browser never commits an image independently from the Stock Master save.
```

For a normal in-process failure:

```text
business fields + ImagePath commit together,
or the Stock Master DB change does not commit.
```

The filesystem and SQL Server cannot be made one physical transaction, so the implementation must use the safe write/rollback/cleanup sequence defined in this plan.

---

# 1. Professional Review Findings Fixed

The earlier plan had a strong foundation but contained several implementation risks. This final plan resolves them.

## 1.1 FIXED — Add permission vs Edit permission conflict

The older design proposed:

- new Stock Master saved with `ADD`;
- image then persisted through a replacement operation normally requiring `EDIT`.

That can reject a legitimate user who has `ADD` but not `EDIT`.

### Final rule

Image persistence is coordinated by `IvStockMasterService.SaveAsync`.

Therefore:

- new item + image uses the existing `ADD` permission;
- existing item + image replacement/removal uses the existing `EDIT` permission;
- no second permission decision is required after the business save.

---

## 1.2 FIXED — partial new-item save could retry as `new`

The older design allowed this state:

```text
item INSERT succeeds
image persistence fails
page remains Mode = "new"
```

A second Save would then attempt another INSERT for the same item code.

### Final rule

Do not use a second image-write call after successful item creation.

The Stock Master service coordinates the image file and DB `ImagePath` during the same Save operation.

This removes the broken `new → partial success → retry new INSERT` state.

---

## 1.3 FIXED — `HasImage` must survive page cloning

Current Stock Master loading does:

```text
service result
   ↓
Clone(result.Data)
   ↓
Model
```

Adding `HasImage` only to `MapEditVm(...)` is insufficient.

### Final rule

When `HasImage` is added, update all relevant mapping/copy paths:

- `IvStockMasterService.MapEditVm(...)`
- `IvStockMasterEntry.Clone(...)`
- `CreateBlank()` / default state
- Copy Item flow: explicitly force `HasImage = false`
- concurrency reload state
- Keep My Changes image-state handling

Failure to update `Clone(...)` is a functional defect.

---

## 1.4 FIXED — IIS application base path

The repository already has the required navigation seam:

- `ErpWeb.UI/Services/AppNavigation.cs`

It exists specifically so root-absolute URLs work when deployed under an IIS sub-application such as:

```text
/erpweb
```

### Final rule

Every item-image browser URL must be built through:

```csharp
Navigation.Resolve(...)
```

Do not hardcode:

```text
/inventory/item-image...
```

directly into the `<img src>` without resolving it.

---

## 1.5 FIXED — current Description UI length mismatch

Current verified repository state:

- `IvStockMaster.IDesc` EF max length: **200**
- `IvStockMasterService.SaveAsync` accepts: **200**
- current Razor `DxTextBox maxlength`: **100**

### Final rule

During this redesign change:

```text
Description maxlength = 200
```

Do not keep the artificial 100-character UI restriction.

This is a UI consistency correction only; no schema change is required.

---

## 1.6 FIXED — image decompression/memory risk

A compressed image can be small on disk and huge when decoded.

The previous 40-million-pixel proposal is unnecessarily high for a normal ERP item image.

### Final limits

```text
Max raw upload            8 MB
Max input pixel count     25,000,000 pixels
Max input side            12,000 px
Max output bounding box   1024 × 1024
Max normalized output     4 MB
WebP quality              85
```

The implementation must identify dimensions/format **before full decode** where the ImageSharp API permits it.

Do not decode a known oversized image first and reject it afterward.

---

## 1.7 FIXED — deterministic IIS storage root

A relative filesystem path must not depend on the IIS worker process working directory.

### Final rule

Inject `IHostEnvironment`.

Resolve `ItemImages:RootPath` as:

```text
absolute/UNC path:
    use as configured

relative path:
    combine with IHostEnvironment.ContentRootPath
```

Default:

```text
{ContentRootPath}/App_Data/item-images
```

---

## 1.8 FIXED — no unnecessary image write API

A separate HTTP POST/DELETE image API is not required for this feature.

### Final rule

Use:

- Blazor `InputFile` for selection;
- server-side `PrepareAsync` for normalization;
- normal Stock Master Save for persistence;
- one authenticated **GET** endpoint for image display.

This reduces attack surface and avoids a second independent write workflow.

---

# 2. Verified Current Repository Facts

These facts were re-checked against the current `productionv2` baseline.

## 2.1 Stock Master files

Existing:

```text
ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor
ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs
ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css
ErpWeb.Core/Inventory/IIvStockMasterService.cs
ErpWeb.Core/Inventory/IvStockMasterService.cs
ErpWeb.Core/Inventory/IvMasterResults.cs
ErpWeb.Model/Entities/Inventory/IvStockMaster.cs
ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs
```

Shared UI chrome:

```text
ErpWeb/wwwroot/css/inventory-chrome.css
```

---

## 2.2 Current form problem

The current page is divided into:

1. Basic
2. Classification
3. Units
4. Inventory
5. Pricing
6. Accounting

Most edit cards contain:

```text
iv-form-grid iv-form-grid--single
```

which forces small inputs to occupy an entire row.

This is the verified cause of much of the wasted horizontal space and unnecessary vertical scrolling.

---

## 2.3 Existing image database column

Current entity already contains:

```csharp
public string? ImagePath { get; set; }
```

Current EF configuration already contains:

```csharp
builder.Property(e => e.ImagePath).HasMaxLength(500);
```

Therefore:

**No new item-image DB column is required.**

Do not add:

- another image-path column;
- an item-image table;
- a migration for a duplicate field.

---

## 2.4 Current Stock Master save behaviour

`IvStockMasterService.SaveAsync(...)` already:

- validates `ADD` vs `EDIT`;
- validates lookups;
- validates Stock/Lot/Expiry rules;
- validates Min Stock <= Max Stock;
- validates RowVersion for edit;
- blocks structural changes when balances/history/drafts exist;
- stamps audit data;
- reloads the saved entity;
- returns `MapEditVm(entity)`.

These behaviours must remain intact.

---

## 2.5 Current Copy Item behaviour

Current page copy logic already clears:

- Item Code
- Barcode
- RowVersion
- Created/Modified audit fields

and resets `MfgType` to BUY display state.

New rule:

```text
Copied item never inherits the source item image.
```

---

## 2.6 Current private attachment architecture

Existing supplier attachment implementation demonstrates the repository's preferred principles:

```text
private filesystem root
generated filenames
path-root validation
tenant validation
temporary file write
authorized access
best-effort physical cleanup
```

Relevant files:

```text
ErpWeb.Core/Purchase/AttachmentStorageOptions.cs
ErpWeb.Core/Purchase/PoSupplierAttachmentService.cs
ErpWeb/Purchase/PoSupplierAttachmentEndpoints.cs
```

The item-image implementation should follow the same security principles but does **not** need to copy its separate upload endpoint workflow.

---

## 2.7 Current app-path architecture

The repository has:

```text
ErpWeb.UI/Services/AppNavigation.cs
```

and `PageBase.Navigation` uses it.

All browser-facing image URLs must respect this seam.

---

## 2.8 Current framework/packages

Verified:

```text
Target framework: .NET 10
DevExpress.Blazor: 26.1.4
Solution: ErpWeb.slnx
```

For image processing, this plan pins:

```text
SixLabors.ImageSharp 4.1.2
```

This version was re-verified as the current stable NuGet package on 2026-10-09 and targets .NET 8+, therefore it is compatible with .NET 10.

Do not float the package version.

---

# 3. Scope

## 3.1 In scope

- professional compact Stock Master Entry redesign;
- one primary item image;
- select/preview/replace/remove;
- safe resize/normalization;
- private filesystem storage;
- authenticated image read;
- Add/Edit permission correctness;
- RowVersion correctness;
- deletion cleanup;
- responsive layout;
- dark/light compatibility;
- tests and regression verification.

## 3.2 Explicitly out of scope

Do not add in this task:

- image gallery;
- multiple images;
- image crop editor;
- drag reorder;
- AI background removal;
- camera capture;
- Stock Master List thumbnails;
- Sales/PO/Production thumbnails;
- e-commerce publishing;
- Azure Blob/S3 abstraction;
- CDN;
- barcode redesign;
- new Stock Master business fields;
- costing changes;
- posting changes;
- UOM conversion changes;
- DR/Production changes.

---

# 4. Approved UI Information Architecture

Do **not** solve the current layout by hiding core Stock Master fields behind many tabs.

Use four visible logical groups.

---

## 4.1 Section A — Item Identity

This is the primary full-width card.

Desktop concept:

```text
┌───────────────────────────────────────────────────────────────────────┐
│ ITEM IDENTITY                                                         │
│                                                                       │
│ ┌──────────────────┐   Item Code     [ ITEM001             ]          │
│ │                  │                                                  │
│ │   ITEM IMAGE     │   Description   [ Industrial Motor........... ] │
│ │                  │                                                  │
│ │   object-fit     │   Barcode       [ 9551234567890 ]                │
│ │     contain      │   Brand         [ Panasonic       ]              │
│ │                  │                                                  │
│ └──────────────────┘   Active [✓]    Supply: BUY — Purchased / Stock │
│ [CHOOSE/REPLACE]                                                       │
│ [REMOVE]                                                               │
└───────────────────────────────────────────────────────────────────────┘
```

Rules:

- image on left at desktop;
- identity fields on right;
- stack image above fields on narrow screens;
- Description is wide;
- Item Code / Barcode / Brand are compact;
- Active does not consume a full row;
- Supply Method stays display-only;
- keep current BUY/MAKE/PHANTOM meaning unchanged.

---

## 4.2 Section B — Product Classification

Fields:

```text
Type
Class
Subclass
Classification
Size
Color
```

Use a compact responsive grid.

Do not create page-width text boxes for Size or Color.

---

## 4.3 Section C — Units & Inventory

Combine current Units + Inventory cards.

### Units subsection

```text
Std UOM
Selling UOM
Purchase UOM
Std Pack Size
Purchase Pack Size
```

### Inventory Control subsection

```text
Stock Control
Lot Control
Expiry Control
Default Warehouse
Default Location
Min Stock
Max Stock
```

Preserve all current dependency behaviour:

```text
LotControl requires StockControl
ExpiryControl = None when LotControl is off
Expiry editor disabled when LotControl is off
Location depends on Warehouse
structural-field lock rules remain unchanged
```

---

## 4.4 Section D — Pricing & Accounting

Combine:

```text
Selling Price
Purchase Price
Selling GL
Purchase GL
Tax Group
Purchase Tax Group
```

Do not change these fields from text editor to lookup in this task.

That would change business semantics and is a separate enhancement.

---

# 5. Exact Compact Layout Strategy

Do not put arbitrary widths on every `Dx*` editor.

Keep DevExpress editors at:

```css
width: 100%;
```

inside sensible grid cells.

Add local Stock Master grid classes in:

```text
IvStockMasterEntry.razor.css
```

Recommended model:

```css
.iv-stock-fields {
    display: grid;
    grid-template-columns: repeat(12, minmax(0, 1fr));
    gap: 12px 14px;
}

.iv-stock-field--xs   { grid-column: span 2; }
.iv-stock-field--sm   { grid-column: span 3; }
.iv-stock-field--md   { grid-column: span 4; }
.iv-stock-field--lg   { grid-column: span 6; }
.iv-stock-field--full { grid-column: 1 / -1; }
```

Exact breakpoints may follow the existing application CSS, but behaviour must be:

### Desktop

```text
>= ~1100 px
12-column compact grid
image + identity side-by-side
```

### Tablet

```text
~768–1099 px
increase field spans
avoid cramped 2-column controls
```

### Mobile

```text
< ~768 px
all fields full width
image stacks above identity
no horizontal page scrolling
```

Use CSS variables already defined by Inventory chrome.

Do not hardcode light-only backgrounds/borders.

---

# 6. Field Span Guidance

The agent may refine spans visually, but should start here.

## Identity

| Field | Span |
|---|---:|
| Item Code | 4 |
| Description | 12 |
| Barcode | 4 |
| Brand | 4 |
| Active | 2–3 |
| Supply Method | remainder / 6 |

## Classification

| Field | Span |
|---|---:|
| Type | 3–4 |
| Class | 3–4 |
| Subclass | 3–4 |
| Classification | 3–4 |
| Size | 3 |
| Color | 3 |

## Units

| Field | Span |
|---|---:|
| Std UOM | 3 |
| Selling UOM | 3 |
| Purchase UOM | 3 |
| Std Pack Size | 3 |
| Purchase Pack Size | 3 |

## Inventory

| Field | Span |
|---|---:|
| Stock Control | 2–3 |
| Lot Control | 2–3 |
| Expiry Control | 3 |
| Warehouse | 3–4 |
| Location | 3–4 |
| Min Stock | 3 |
| Max Stock | 3 |

## Pricing & Accounting

| Field | Span |
|---|---:|
| Selling Price | 3 |
| Purchase Price | 3 |
| Selling GL | 3–4 |
| Purchase GL | 3–4 |
| Tax Group | 3 |
| Purchase Tax Group | 3 |

Do not force a layout that becomes cramped simply to meet the table above.

User readability has priority.

---

# 7. UI Field Corrections

## 7.1 Description max length

Change current:

```text
maxlength="100"
```

to:

```text
maxlength="200"
```

to match:

- EF configuration;
- current server validation.

## 7.2 Do not silently change other business lengths

Preserve existing backend limits.

Do not invent new max lengths during UI redesign.

---

# 8. Item Image Functional Rules

## 8.1 One primary image

Exactly one current primary image per item.

DB authority:

```text
IvStockMaster.ImagePath
```

No gallery.

---

## 8.2 Supported input formats

Accept only actual:

```text
JPEG
PNG
WebP
```

Reject:

```text
GIF
SVG
BMP
TIFF
HEIC/HEIF
PDF
Office documents
unknown/invalid content
```

The HTML `accept` attribute is convenience only.

Security validation must happen server-side.

---

## 8.3 Authoritative format detection

Do not trust:

- filename extension;
- browser `Content-Type`.

Use ImageSharp identify/decode result as the authoritative image format.

The extension/content type can be used only as preliminary UX validation.

---

## 8.4 Raw upload limit

Hard limit:

```text
8 MB
```

When using `IBrowserFile`:

```csharp
file.OpenReadStream(maxAllowedSize: configuredLimit)
```

must use the configured hard limit.

Do not increase the application's global SignalR maximum receive message size to support this feature.

`InputFile` streaming should remain bounded to this one file.

---

# 9. Image Processing Rules

Create the normalized image when the user selects it.

Approved processing pipeline:

```text
1. Check declared file size <= 8 MB
2. Open bounded stream
3. Identify image format + width + height before full decode
4. Reject unsupported format
5. Reject side > 12,000 px
6. Reject width × height > 25,000,000 pixels
7. Decode
8. Reject multi-frame/animated image
9. AutoOrient()
10. Remove EXIF/XMP/IPTC/GPS/nonessential metadata
11. Resize only when width > 1024 OR height > 1024
12. Resize with Max/contain semantics
13. Never upscale
14. Encode WebP quality 85
15. Reject normalized output > 4 MB
16. return server-generated IvPreparedStockImage
```

Important order:

```text
AutoOrient BEFORE deleting EXIF orientation metadata.
```

---

## 9.1 Resize examples

| Source | Expected output |
|---|---|
| 3000×2000 | 1024×683 |
| 2000×3000 | 683×1024 |
| 800×600 | 800×600 |
| 500×500 | 500×500 |

No crop.

No stretch.

---

## 9.2 Why input pixel ceiling is 25 million

A compressed phone image may decode to large in-memory buffers.

For ERP identification:

- normal 12 MP phone images are accepted;
- normal 24 MP images are accepted;
- unusually large 48/64/108 MP captures are rejected instead of risking large transient server memory.

This is intentional.

---

# 10. Image Package

Modify:

```text
ErpWeb.Core/ErpWeb.Core.csproj
```

Add pinned:

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="4.1.2" />
```

Do not use:

```text
System.Drawing.Common
```

for server image processing.

Before final completion, run the normal dependency/license/vulnerability checks used by the project.

At minimum, inspect the new package with the installed SDK's package-vulnerability command if supported.

---

# 11. Image Options

Create:

```text
ErpWeb.Core/Inventory/ItemImageStorageOptions.cs
```

Recommended:

```csharp
public sealed class ItemImageStorageOptions
{
    public const string SectionName = "ItemImages";

    public string RootPath { get; set; } =
        Path.Combine("App_Data", "item-images");

    public long MaxUploadBytes { get; set; } = 8L * 1024 * 1024;
    public int MaxInputPixels { get; set; } = 25_000_000;
    public int MaxInputSide { get; set; } = 12_000;
    public int MaxDimension { get; set; } = 1024;
    public int MaxProcessedBytes { get; set; } = 4 * 1024 * 1024;
    public int WebpQuality { get; set; } = 85;
}
```

Names can be adjusted to project convention.

---

## 11.1 Options validation

Bind in:

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

Use startup validation.

Validate at least:

```text
RootPath non-empty
MaxUploadBytes > 0
MaxInputPixels > 0
MaxInputSide > 0
MaxDimension between sensible safe bounds
MaxProcessedBytes > 0
WebpQuality 1..100
```

Invalid production configuration should fail clearly on application startup, not on the first user upload.

---

# 12. Private Storage

## 12.1 Root

Default resolved root:

```text
{ContentRootPath}/App_Data/item-images
```

Images must not be placed in `wwwroot`.

---

## 12.2 Absolute / UNC configuration

If configured `RootPath` is:

- absolute drive path; or
- UNC/shared path;

use it as configured.

This permits a shared location if the application is ever deployed to multiple IIS nodes.

Do not implement cloud storage abstraction in this task.

---

## 12.3 Relative path

If configured path is relative:

```csharp
Path.Combine(hostEnvironment.ContentRootPath, configuredPath)
```

then call:

```csharp
Path.GetFullPath(...)
```

Do not depend on process current working directory.

---

# 13. Managed File Path

Do not use raw `ICode` as a directory name.

Recommended:

```text
{CompanySegment}/{ItemHash}/{GuidN}.webp
```

Where:

```text
CompanySegment
    validated safe tenant segment
    OR deterministic safe encoding

ItemHash
    SHA-256 of normalized trimmed item code
    hex string

GuidN
    Guid.NewGuid().ToString("N")
```

Example:

```text
DEMO/9F8C...E210/18df28f13e3748fc83d98ed2ce77da24.webp
```

Use a full deterministic safe hash unless there is a concrete reason to truncate it.

---

## 13.1 DB path

`IvStockMaster.ImagePath` stores only:

```text
server-generated relative managed path
```

Never:

- absolute disk path;
- browser filename;
- browser supplied path;
- URL.

Before assignment, assert:

```text
relative path length <= 500
```

because the existing EF column length is 500.

---

# 14. Path Safety

Implement a reusable storage-root guard equivalent in principle to the supplier attachment service.

For every read/delete/write path:

```text
normalize root
normalize candidate
candidate must remain under root
```

Reject or safely ignore:

```text
..
absolute path from DB/client
escaped separators
path outside managed root
```

Even values read from the DB must be treated defensively.

---

# 15. Core Image Service

Create:

```text
ErpWeb.Core/Inventory/IIvStockMasterImageService.cs
ErpWeb.Core/Inventory/IvStockMasterImageService.cs
```

The service owns:

```text
image preparation
managed filesystem write
managed filesystem read
managed filesystem cleanup
tenant/access-aware image read
```

It does **not** accept a client-controlled `ImagePath`.

It does **not** independently update `IvStockMaster.ImagePath`.

The Stock Master Save owns the DB update.

---

# 16. Core Image Models

Add focused types in the Inventory core area.

Example:

```csharp
public sealed class IvPreparedStockImage
{
    public required byte[] Content { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string ContentType { get; init; } = "image/webp";
}

public sealed class IvStoredStockImage
{
    public required string RelativePath { get; init; }
}

public sealed class IvStockMasterImageReadResult
{
    public required Stream Stream { get; init; }
    public string ContentType { get; init; } = "image/webp";
}
```

Exact record/class style may follow repository conventions.

---

# 17. Image Preparation Contract

Recommended:

```csharp
Task<IvMasterOperationResult<IvPreparedStockImage>> PrepareAsync(
    string originalFileName,
    string? contentType,
    Stream content,
    long contentLength,
    CancellationToken cancellationToken = default);
```

Preparation:

- does not alter DB;
- does not write permanent file;
- returns normalized bounded bytes;
- exposes no physical path.

---

# 18. Managed File Store Contract

Recommended internal responsibility in the same service:

```csharp
Task<IvMasterOperationResult<IvStoredStockImage>> StorePreparedAsync(
    string companyCode,
    string itemCode,
    IvPreparedStockImage image,
    CancellationToken cancellationToken = default);

Task TryDeleteManagedFileAsync(
    string? relativePath,
    string companyCode,
    string itemCode,
    string operation,
    CancellationToken cancellationToken = default);
```

`StorePreparedAsync`:

- receives company/item values only from trusted server business code;
- creates directory safely;
- writes to temp file in same final directory;
- flushes/closes;
- moves temp → final generated name;
- never overwrites an existing file.

---

# 19. Image Read Contract

Recommended:

```csharp
Task<IvMasterOperationResult<IvStockMasterImageReadResult>> OpenReadAsync(
    string itemCode,
    CancellationToken cancellationToken = default);
```

`OpenReadAsync` must:

1. validate current tenant;
2. require Inventory Item Master `ACCESS`;
3. load item by `CompanyCode + ICode`;
4. return NotFound if no `ImagePath`;
5. validate path is managed and under root;
6. return NotFound if physical file missing;
7. open stream read-only;
8. return `image/webp`;
9. never reveal the path.

---

# 20. Image Change Command

Add a server-side command passed to Stock Master Save.

Example:

```csharp
public sealed class IvStockMasterImageChange
{
    public IvPreparedStockImage? Replacement { get; init; }
    public bool RemoveExisting { get; init; }

    public bool HasChange =>
        Replacement is not null || RemoveExisting;
}
```

Validation:

```text
Replacement != null AND RemoveExisting == true
    => reject programmer/state error
```

The command contains image bytes/state only.

It contains **no ImagePath**.

---

# 21. Stock Master Service Contract

Keep the current method for all existing callers.

Current:

```csharp
Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
    IvStockMasterEditVm model,
    bool isNew,
    CancellationToken cancellationToken = default);
```

Add an overload rather than breaking positional cancellation-token callers:

```csharp
Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
    IvStockMasterEditVm model,
    bool isNew,
    IvStockMasterImageChange? imageChange,
    CancellationToken cancellationToken = default);
```

Existing overload delegates:

```text
SaveAsync(model, isNew, imageChange: null, cancellationToken)
```

Do not reorder the existing parameters in a way that can silently break current call sites.

---

# 22. Stock Master Save — Final Transaction Sequence

Modify:

```text
ErpWeb.Core/Inventory/IvStockMasterService.cs
```

The exact current business validation stays first.

---

## 22.1 New item + optional image

Required sequence:

```text
1. Validate model
2. Validate ADD permission
3. Validate lookup/business rules
4. Validate duplicate code
5. Validate write scope
6. If replacement requested:
       StorePreparedAsync(...)
       capture newRelativePath
   If store fails:
       return failure
       DO NOT insert Stock Master
7. Create IvStockMaster
8. Apply current editable business fields
9. Set ImagePath = newRelativePath ONLY from managed storage result
10. SaveChanges
11. If DB SaveChanges fails:
       delete newly stored file best-effort
       rethrow/return existing failure semantics
12. Reload entity
13. Return MapEditVm(entity)
```

This allows:

```text
ADD permission + new image
```

without requiring `EDIT`.

---

## 22.2 Existing item — no image change

Required:

```text
exactly current behaviour
ImagePath untouched
filesystem service not called
```

Normal edits must never clear the image.

---

## 22.3 Existing item — replacement

Sequence:

```text
1. Validate model
2. Validate EDIT permission
3. Validate business/lookups
4. Load current item by Company + ICode
5. Validate RowVersion
6. Validate structural-field rules
7. Capture oldImagePath
8. Store new prepared image
9. Set entity.ImagePath = new managed relative path
10. Apply normal business fields
11. SaveChanges
12. If DB save fails:
       delete new file
       leave old file untouched
13. Reload entity
14. After successful DB save:
       delete old file best-effort
15. Return new MapEditVm
```

### Important

Do not write a new file before:

- business validation;
- current-row lookup;
- RowVersion check;
- structural validation.

A stale or invalid form must not create an orphan image file.

---

## 22.4 Existing item — remove

Sequence:

```text
1. validate EDIT permission + all normal save rules
2. validate RowVersion
3. capture oldImagePath
4. set ImagePath = null
5. SaveChanges
6. reload entity
7. after DB success:
       delete old file best-effort
8. return MapEditVm
```

If physical file is already missing:

```text
DB removal still succeeds
cleanup logs at most a diagnostic
```

---

# 23. Filesystem / DB Failure Invariant

Because filesystem + SQL are not one ACID transaction, use this invariant:

### Replacement

```text
new file created before DB points to it
old file retained until DB succeeds
```

### DB failure

```text
delete new file best-effort
old DB path + old file remain
```

### DB success

```text
DB points to new path
then old file is removed best-effort
```

This prevents a normal handled exception from leaving the item pointing at a file that was never successfully created.

A process crash can theoretically leave an orphan new file.

That is acceptable for this scope.

A future orphan-reconciliation maintenance job is explicitly out of scope.

---

# 24. Image State in Edit VM

Modify:

```text
ErpWeb.Core/Inventory/IvMasterResults.cs
```

Add:

```csharp
public bool HasImage { get; set; }
```

Do **not** add editable/public:

```csharp
public string? ImagePath { get; set; }
```

to the UI edit VM.

---

# 25. MapEditVm

Modify:

```text
IvStockMasterService.MapEditVm(...)
```

Add:

```csharp
HasImage = !string.IsNullOrWhiteSpace(x.ImagePath)
```

---

# 26. ApplyEditableFields — hard security rule

Do not add:

```csharp
entity.ImagePath = model.ImagePath;
```

Do not add any client path mapping.

`ApplyEditableFields(...)` should continue handling normal business fields.

`ImagePath` is changed only in the coordinated Save image-change branch.

---

# 27. Page Clone / Copy Rules

Modify:

```text
IvStockMasterEntry.razor.cs
```

## Clone

Add:

```text
HasImage = source.HasImage
```

## Blank

Default:

```text
HasImage = false
```

## Copy Item

After cloning source:

```text
HasImage = false
pending image = null
pending removal = false
```

Do not copy source file.

Do not copy source `ImagePath`.

---

# 28. Page Image State

Recommended fields:

```csharp
private IvPreparedStockImage? _pendingImage;
private bool _pendingImageRemoval;
private string? _pendingImagePreviewDataUrl;
private string? _imageError;
private bool _isPreparingImage;
private int _imageInputKey;
```

Names may follow project conventions.

---

# 29. Page Image State Machine

This must be deterministic.

## 29.1 Existing persisted image, no pending change

Display:

```text
current endpoint image
buttons: REPLACE, REMOVE
```

---

## 29.2 New pending replacement

Display:

```text
pending normalized preview
```

If an existing image was present:

```text
buttons: CHOOSE ANOTHER, UNDO REPLACEMENT
secondary REMOVE CURRENT IMAGE if UX remains clear
```

Preferred simplest behaviour:

```text
UNDO REPLACEMENT
    restores currently persisted image
```

Then user may click Remove separately.

---

## 29.3 Pending removal

Display:

```text
placeholder / "Image will be removed when you save"
button: UNDO REMOVE
```

Do not delete anything yet.

---

## 29.4 New item pending image

Display pending preview.

Remove/clear:

```text
clears pending image
returns to no-image placeholder
does not set RemoveExisting
```

---

## 29.5 Choose another image

Replace `_pendingImage` with the newly prepared one.

Release references to the old pending bytes/preview as soon as possible.

---

# 30. Dirty State

Current dirty state uses model snapshot.

Extend:

```text
IsDirty =
    current model snapshot != clean model snapshot
    OR _pendingImage != null
    OR _pendingImageRemoval
```

Changing only the image must trigger the existing discard confirmation.

---

# 31. Image Selection

Use:

```text
Microsoft.AspNetCore.Components.Forms.InputFile
```

Recommended:

```razor
<InputFile OnChange="@OnImageSelectedAsync"
           accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp" />
```

Style it with a local wrapper.

Do not add a heavy JavaScript upload library.

A small CSS wrapper / native file input is preferred.

If a tiny JS helper is absolutely required to trigger a hidden picker, keep it local and base-path safe; do not implement upload through JS.

---

# 32. File Input UX

The visible action should read:

```text
CHOOSE IMAGE
```

when none exists, and:

```text
REPLACE IMAGE
```

when an image exists.

After invalid selection:

- show `_imageError` next to the image panel;
- allow selecting the same filename again.

If necessary, increment `_imageInputKey` to force a fresh input element.

---

# 33. Selection Handler

`OnImageSelectedAsync(...)`:

```text
1. ignore while submitting/preparing
2. clear previous image error
3. read exactly one file
4. reject file.Size > configured max immediately
5. open bounded stream with maxAllowedSize
6. call ImageService.PrepareAsync(...)
7. on failure:
       keep existing persisted image state
       show friendly image error
8. on success:
       replace pending prepared image
       clear pending remove
       create pending preview
9. release stream
10. re-enable Save
```

Disable Save while image preparation is in progress.

This prevents Save racing ahead of image preparation.

---

# 34. Pending Preview

The preview should reflect the normalized image.

A data URL is acceptable because the normalized image is:

- max 1024×1024;
- max 4 MB;
- one image only.

Build once after preparation:

```text
data:image/webp;base64,...
```

Do not repeatedly convert the byte array during every render.

Clear the data URL and pending bytes after:

- successful save;
- cancel/navigation;
- reload latest;
- choosing another file;
- component disposal if implemented.

---

# 35. Blazor Server Memory Gate

Do not retain:

- original 8 MB browser bytes;
- decoded `Image` object;
- multiple pending image copies.

After preparation, keep only:

```text
normalized prepared WebP bytes
preview string if required
```

The decoded ImageSharp object must be disposed immediately.

Do not increase global SignalR message limits.

---

# 36. Save Handler

Current page calls:

```text
StockMasters.SaveAsync(Model, IsNewMode)
```

Update to build an image change command.

Concept:

```csharp
IvStockMasterImageChange? imageChange = null;

if (_pendingImage is not null)
{
    imageChange = new IvStockMasterImageChange
    {
        Replacement = _pendingImage
    };
}
else if (_pendingImageRemoval)
{
    imageChange = new IvStockMasterImageChange
    {
        RemoveExisting = true
    };
}

var result = await StockMasters.SaveAsync(
    Model,
    IsNewMode,
    imageChange);
```

There is no separate post-save image commit.

---

# 37. Save Success

On success:

```text
clear pending image references
navigate to /inventory/items
```

Maintain the existing successful-save navigation behaviour.

Do not introduce a special image-save page state.

---

# 38. Save Failure

If save fails:

- stay on page;
- preserve pending image;
- preserve image preview;
- show normal business or image error;
- DB must not have partially saved normal form changes due solely to image commit ordering.

Concurrency:

- show current concurrency popup;
- pending image remains until user chooses Reload Latest or Cancel.

---

# 39. Concurrency Popup — image-specific rules

## Reload Latest

Current action replaces the model with latest DB state.

Also:

```text
clear pending replacement
clear pending removal
clear pending preview
clear image error
load latest HasImage
```

Reload Latest means discard all local edits, including image edits.

---

## Keep My Changes

Current behaviour adopts latest RowVersion while preserving user form edits.

New behaviour:

```text
preserve pending image change
adopt latest RowVersion
update Model.HasImage from the latest persisted record
```

If latest user removed the current image while this user has a pending replacement:

```text
pending replacement remains valid
```

If this user had pending removal but latest item already has no image:

```text
clear redundant pending removal
```

Then user can Save again with the latest RowVersion.

---

# 40. Current Image URL

Add helper in page code-behind.

Concept:

```csharp
protected string BuildItemImageUrl()
{
    var route =
        $"/inventory/item-image?iCode={Uri.EscapeDataString(Model.ICode)}";

    return Navigation.Resolve(route);
}
```

Do not concatenate the IIS application base path manually.

Do not use a physical path.

---

# 41. Image Read Endpoint

Create:

```text
ErpWeb/Inventory/IvStockMasterImageEndpoints.cs
```

Map in:

```text
ErpWeb/Program.cs
```

Recommended:

```text
GET /inventory/item-image?iCode=...
```

Query parameter is preferred over a route parameter because item codes may contain characters awkward for route segments.

---

# 42. Read Endpoint Security

Endpoint requirements:

```text
RequireAuthentication
Inventory Item Master ACCESS checked in service
tenant company isolation
CompanyCode + ICode lookup
managed path validation
physical file existence
Content-Type = image/webp
no physical path returned
```

Return:

```text
403  no permission
404  item/image/file not found
400  invalid scope/input where applicable
```

Unexpected read errors:

```text
log details
return generic server error
```

---

# 43. Image Response Headers

Because the URL does not carry tenant identity and users may switch accounts in one browser, use conservative caching.

Recommended:

```text
Cache-Control: private, no-store
X-Content-Type-Options: nosniff
```

Do not rely on a RowVersion query parameter as the only protection against stale/cross-login browser cache.

With `no-store`, a cache-busting RowVersion query is unnecessary.

---

# 44. Item Delete Cleanup

Current delete logic:

```text
IvStockMasterService.DeleteAsync(...)
```

Add cleanup without changing reference protection.

Sequence:

```text
1. perform current permission / RowVersion / reference checks
2. before remove, capture each entity.ImagePath
3. delete DB entities
4. SaveChanges
5. Commit transaction
6. AFTER successful DB commit:
       delete captured managed image files best-effort
7. return current success
```

If delete is blocked:

```text
do not touch image files
```

If DB delete rolls back:

```text
do not touch image files
```

If physical cleanup fails after DB commit:

```text
log
do not turn successful item deletion into failure
```

---

# 45. Cleanup Interface

To avoid making Stock Master delete depend on image-processing details, the image service may expose a narrow cleanup contract.

Example:

```text
IIvStockMasterImageFileCleanup
```

and:

```csharp
Task TryDeleteManagedFileAsync(...)
```

Register the same scoped implementation under both interfaces if helpful, mirroring the existing supplier attachment cleanup pattern.

---

# 46. DI Registration

Modify:

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

Add:

```text
ItemImageStorageOptions binding + validation
IIvStockMasterImageService
optional IIvStockMasterImageFileCleanup alias
```

Keep:

```text
IIvStockMasterService -> IvStockMasterService
```

Do not change the normal service lifetime.

---

# 47. Avoid Circular Dependency

Required dependency direction:

```text
IvStockMasterService
       ↓
IIvStockMasterImageService / cleanup

IvStockMasterImageService
       ↓
DbContextFactory / tenant / access rights / options / host environment / logger
```

Forbidden:

```text
IvStockMasterImageService
       ↓
IIvStockMasterService
```

That would create a circular dependency.

---

# 48. UI Image Panel

Add local CSS classes such as:

```text
.iv-stock-identity
.iv-stock-image-panel
.iv-stock-image-frame
.iv-stock-image
.iv-stock-image-placeholder
.iv-stock-image-actions
.iv-stock-image-error
.iv-stock-fields
.iv-stock-subsection
.iv-stock-toggle-row
```

Preview size target:

```text
180–220 px square visual frame
```

Image CSS:

```css
width: 100%;
height: 100%;
object-fit: contain;
```

No crop.

---

# 49. Image Placeholder

When no image:

```text
neutral image icon
"No item image"
```

Do not show a broken `<img>` element as the normal placeholder.

Use theme variables.

---

# 50. View Mode

View mode must use the same new information architecture.

Groups:

```text
Item Identity + image
Product Classification
Units & Inventory
Pricing & Accounting
Audit
```

If `HasImage`:

```text
show current image
```

Else:

```text
show clean placeholder
```

No image-edit buttons in View mode.

---

# 51. Optional Large Preview

A click-to-large-preview popup is optional.

It is **not** required to complete this task.

If implemented:

```text
simple popup
object-fit contain
max viewport dimensions
Close action
no crop/edit tools
```

Do not delay core implementation for it.

---

# 52. Image Action Permissions in UI

Load/derive both relevant rights if needed:

```text
CanAdd
CanEdit
```

New mode:

```text
image selection visible only when ADD is available
```

Edit mode:

```text
replace/remove visible only when EDIT is available
```

View mode:

```text
display only
```

Final server-side Save remains authoritative.

Hiding buttons is not a security boundary.

---

# 53. DevExpress Binding Safety

Mandatory repository skill:

```text
.agents/skills/devexpress-blazor-editor-binding-safety/skill.md
```

Audit every editor in the modified form.

Rules:

```text
normal editor:
    use @bind-*

custom change handler:
    Property
    PropertyChanged
    matching PropertyExpression

never:
    @bind-* + matching *Changed

never:
    disable validation just to suppress expression exception
```

Especially preserve the current correct patterns for:

```text
LotControl
ExpiryControl
Class dependent change
Warehouse dependent change
```

---

# 54. InputFile Is Not a DevExpress Editor

`InputFile` does not use DevExpress `ValueExpression` rules.

Do not attempt to force DevExpress binding conventions onto it.

Keep it outside the normal editable model binding.

---

# 55. Page Save/Cancel Convention

Preserve the existing Stock Master footer:

```text
CANCEL
SAVE
```

Do not introduce separate:

```text
SAVE IMAGE
UPLOAD IMAGE TO SERVER
COMMIT IMAGE
```

The item has one Save operation.

This is important for intuitive ERP behaviour.

---

# 56. Validation UX

Image error examples:

```text
Only JPG, PNG, or WebP images are allowed.
Image exceeds the 8 MB upload limit.
Image dimensions are too large.
The selected file is not a valid image.
Unable to store the item image. The item was not saved.
```

Do not display:

- stack trace;
- physical path;
- ImageSharp decoder exception;
- server drive name.

---

# 57. Existing Business Validation Must Remain

Do not weaken:

```text
Item Code required
Description required
Selling GL required
Classification required
active lookup validation
Subclass belongs to Class
Location belongs to Warehouse
Lot requires Stock Control
Expiry rules
Min <= Max
RowVersion concurrency
structural-field lock
```

The image feature is orthogonal to these rules.

---

# 58. Existing Image Preservation

Normal Save with:

```text
imageChange == null
```

must leave:

```text
entity.ImagePath
```

exactly unchanged.

Add an automated regression test for this.

---

# 59. Legacy/Unmanaged ImagePath Safety

If an existing DB row contains an `ImagePath` that is not under the new managed root:

```text
do not attempt to serve an arbitrary file
do not delete an arbitrary file
return image NotFound / unavailable
log a safe warning if useful
leave DB value untouched unless the user replaces/removes it
```

Do not add a legacy path migration in this task.

---

# 60. Image Storage Logging

Structured logs for failures.

Include safe:

```text
CompanyCode
ItemCode
operation
managed relative path where appropriate
```

Operations:

```text
prepare
store
read
replace-cleanup
remove-cleanup
item-delete-cleanup
```

Do not log:

```text
image bytes
base64 preview
raw EXIF
client local filesystem path
absolute server path in user-facing messages
```

Absolute server path may be present in internal exception/log context only if existing logging policy permits it.

---

# 61. UI Performance

Do not:

```text
load image bytes in StockMaster GetAsync
store image base64 in DB
resize image on every GET
load original upload after preparation
```

`GetAsync` returns only:

```text
HasImage
```

The browser reads the image through the authenticated endpoint.

---

# 62. Accessibility

Required:

```text
meaningful alt text
keyboard-accessible choose/replace/remove
visible focus state
text label for destructive Remove
image error text near image area
placeholder distinguishable from real image
dark/light readable
```

Suggested alt:

```text
"{ItemCode} - {Description}"
```

Razor encoding must remain enabled.

---

# 63. Files Expected to Change

## Existing

```text
ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor
ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs
ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css

ErpWeb.Core/Inventory/IIvStockMasterService.cs
ErpWeb.Core/Inventory/IvStockMasterService.cs
ErpWeb.Core/Inventory/IvMasterResults.cs
ErpWeb.Core/CoreServiceCollectionExtensions.cs
ErpWeb.Core/ErpWeb.Core.csproj

ErpWeb/Program.cs

ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs
```

Potential config change only if explicit overrides are desired:

```text
ErpWeb/appsettings*.json
```

Defaults must work without config entry.

---

# 64. New Files

Recommended:

```text
ErpWeb.Core/Inventory/ItemImageStorageOptions.cs
ErpWeb.Core/Inventory/IIvStockMasterImageService.cs
ErpWeb.Core/Inventory/IvStockMasterImageService.cs
ErpWeb.Core/Inventory/IIvStockMasterImageFileCleanup.cs   (optional narrow contract)

ErpWeb/Inventory/IvStockMasterImageEndpoints.cs

ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs
```

The agent may co-locate small result records with the interface if that matches repository style.

Do not create excessive one-type-per-file ceremony without benefit.

---

# 65. Areas That Must Not Be Changed

Unless compilation proves a direct dependency, do not change:

```text
Inventory posting
Stock ledger
Costing
FIFO CostLayer
Standard cost
Period close / month end
Balances
Lot allocation
Stock movement rules
Sales posting
Procurement posting
Production posting
Work Order logic
Delivery Request logic
Production costing
UOM conversion logic
```

No business calculation belongs in this UI/image enhancement.

---

# 66. Database Rule

No new DB migration should be needed because the current entity already maps:

```text
IvStockMaster.ImagePath
```

Before deployment, verify target database schema actually contains the existing `ImagePath` column.

If a target database is missing it, treat that as deployment/schema drift and follow the project's normal schema patch process.

Do not create a duplicate image field.

---

# 67. Automated Test Plan — Processor

Required tests:

1. valid JPG accepted;
2. valid PNG accepted;
3. valid WebP accepted;
4. renamed invalid file rejected;
5. GIF rejected;
6. SVG rejected;
7. >8 MB input rejected;
8. >25M input pixels rejected before full decode where possible;
9. >12,000 side rejected;
10. landscape >1024 resized correctly;
11. portrait >1024 resized correctly;
12. smaller image not upscaled;
13. aspect ratio preserved;
14. EXIF orientation applied;
15. EXIF metadata removed afterward;
16. multi-frame image rejected;
17. normalized output is WebP;
18. normalized output <=4 MB;
19. output width/height <=1024;
20. malformed data returns friendly validation failure.

---

# 68. Automated Test Plan — File Store

Use a unique temporary directory per test.

Never write test images into repository `App_Data`.

Required:

1. generated relative path remains under root;
2. raw ItemCode is not used as path;
3. path length <=500;
4. generated file name cannot overwrite an existing file;
5. `..` escape rejected;
6. absolute injected relative-path value rejected on read/delete;
7. missing cleanup file is idempotent;
8. failed write leaves no `.tmp`;
9. store returns only relative path;
10. content read back is the same normalized WebP.

---

# 69. Automated Test Plan — Stock Master Save

Extend current service tests.

Required:

### Existing image mapping

1. `GetAsync` => `HasImage=true` when DB ImagePath is populated.
2. `GetAsync` => false when null/blank.
3. normal Save without image change preserves existing ImagePath.

### New item

4. new item + pending image saves item and ImagePath.
5. user with `ADD=true`, `EDIT=false` can create item with image.
6. image storage failure means new item is not inserted.
7. duplicate/validation failure happens before permanent image store.
8. generated path only is persisted.

### Existing item

9. image replacement requires EDIT.
10. stale RowVersion fails before new file is stored.
11. replacement updates ImagePath.
12. replacement preserves old file until DB success.
13. DB save failure cleans new file and does not delete old.
14. successful DB update deletes old file best-effort.
15. remove clears ImagePath.
16. remove with missing physical old file still succeeds.
17. normal form edit with no image change never calls image store.

### Business regression

18. structural-field locks remain unchanged.
19. existing lookup validations remain unchanged.
20. Min/Max validation remains unchanged.
21. lot/expiry validation remains unchanged.

---

# 70. RowVersion Test Note

The production database uses SQL Server rowversion semantics.

If the current SQLite unit-test provider cannot faithfully auto-advance SQL Server rowversion:

- do not fake a passing assertion that proves nothing;
- keep existing concurrency test strategy;
- assert the service uses the returned/reloaded current token where the provider supports it;
- add a SQL Server integration/manual gate for actual rowversion advancement if required.

The implementation must still preserve the production RowVersion behaviour.

---

# 71. Automated Test Plan — Read Security

Required:

1. ACCESS user can read own-company image;
2. no ACCESS => denied;
3. same ItemCode in another company cannot be read;
4. no image => NotFound;
5. unmanaged path => NotFound;
6. missing file => NotFound;
7. response content type is WebP;
8. physical relative/absolute path is never returned in payload.

---

# 72. Automated Test Plan — Delete Cleanup

Required:

1. unused item delete succeeds then image cleanup is called;
2. referenced item delete blocked and cleanup is not called;
3. concurrency delete failure does not cleanup image;
4. DB delete exception does not cleanup image;
5. physical cleanup failure after DB commit does not turn DB delete into failure.

---

# 73. UI Manual Test — New Item

1. open `/inventory/items/new`;
2. verify compact professional layout;
3. Description permits >100 up to 200 chars;
4. choose 3000×2000 JPEG;
5. processing indicator appears;
6. normalized preview appears;
7. Save disabled during preparation;
8. Save item;
9. return to list;
10. reopen item;
11. image displays;
12. DB path is managed relative WebP path;
13. stored image is <=1024 bounding box.

---

# 74. UI Manual Test — Add-only Permission

Test account:

```text
ACCESS = yes
ADD = yes
EDIT = no
```

Required:

```text
can create item
can select image in New mode
can save item + image
cannot edit/replace image afterward without EDIT
```

This is a mandatory gate because it fixes a blocker in the earlier design.

---

# 75. UI Manual Test — Replace

1. edit existing image item;
2. choose replacement;
3. preview replacement;
4. click Cancel;
5. reopen;
6. old image still exists;
7. repeat replacement;
8. Save;
9. reopen;
10. replacement exists;
11. old file removed.

---

# 76. UI Manual Test — Pending Replacement Undo

1. existing image;
2. choose replacement;
3. click Undo Replacement;
4. current persisted image returns;
5. Save with no other changes;
6. no image store/delete occurs.

---

# 77. UI Manual Test — Remove

1. existing image;
2. Remove;
3. UI shows pending-removal state;
4. Cancel;
5. old image remains;
6. repeat Remove;
7. Save;
8. DB ImagePath null;
9. old managed file deleted.

---

# 78. UI Manual Test — Copy

1. source item has image;
2. choose Copy Item;
3. New Item screen shows no inherited image;
4. save copy without selecting image;
5. new item has no ImagePath;
6. source image remains unchanged.

---

# 79. UI Manual Test — Concurrency

Two sessions edit same item.

Session A:

```text
changes image and/or fields
save
```

Session B:

```text
has stale RowVersion
attempt save with pending image
```

Required:

```text
concurrency conflict
no new permanent image created for stale save
pending local image remains for user decision
Reload Latest discards pending image
Keep My Changes adopts latest RowVersion and preserves pending image
```

---

# 80. UI Manual Test — Responsive

Verify approximately:

```text
1440 px
1200 px
1024 px
768 px
390 px
```

Requirements:

```text
no horizontal page scroll
labels remain readable
image panel does not dominate
short fields are not huge
Save/Cancel remain reachable
mobile fields stack cleanly
```

---

# 81. UI Manual Test — Keyboard

Tab through the entire form.

Required:

```text
focus order == visual order
image choose action keyboard reachable
Remove keyboard reachable
no CSS order/reordering mismatch
```

Do not use CSS `order` to visually move inputs away from DOM focus sequence.

---

# 82. UI Manual Test — Dark/Light

Verify both supported themes.

Required:

```text
image placeholder visible
image border visible
required-field background still visible
labels readable
Remove action readable
no hard-coded white panel that breaks dark mode
```

---

# 83. App Base Path Test

Run or verify under an IIS-style base path:

```text
/erpweb
```

Required:

```text
Stock Master page loads
image src resolves under /erpweb
GET image succeeds
no request incorrectly goes to site-root /inventory/item-image
```

This test is mandatory.

---

# 84. Build Gate

From repository root:

```bash
dotnet restore ErpWeb.slnx
dotnet build ErpWeb.slnx
dotnet test ErpWeb.slnx
```

Do not report completion with a compilation error.

---

# 85. DevExpress Runtime Gate

After build, open:

```text
New mode
Edit mode
View mode
```

Change every affected DevExpress editor.

Required:

```text
no TextExpression exception
no ValueExpression exception
no CheckedExpression exception
no duplicate ValueChanged parameter
validation still displays
dependent Class/Subclass works
dependent Warehouse/Location works
Lot/Expiry works
```

---

# 86. Implementation Sequence

## Phase 1 — Baseline guard

1. checkout/confirm `productionv2`;
2. record HEAD;
3. compare with verified baseline;
4. re-open touched files;
5. search for any newer image work;
6. stop and reconcile if another implementation already changed the same contract.

---

## Phase 2 — Image processing/storage core

1. add ImageSharp 4.1.2;
2. add options + startup validation;
3. add prepared/stored image types;
4. implement `PrepareAsync`;
5. implement safe managed store;
6. implement safe cleanup;
7. implement read service;
8. add processor/storage tests;
9. build/test.

Do not start UI redesign until this phase compiles and core tests pass.

---

## Phase 3 — Stock Master service integration

1. add `IvStockMasterImageChange`;
2. add non-breaking `SaveAsync` overload;
3. inject image service/storage;
4. integrate new-item image sequence;
5. integrate edit replacement;
6. integrate removal;
7. guarantee no-image-change path leaves ImagePath untouched;
8. integrate delete cleanup;
9. update service tests;
10. build/test.

---

## Phase 4 — Edit VM + mappings

1. add `HasImage`;
2. update `MapEditVm`;
3. ensure List projection does not load bytes;
4. do not expose ImagePath;
5. build/test.

---

## Phase 5 — Read endpoint

1. add GET endpoint;
2. map in `Program.cs`;
3. add safe response headers;
4. test Access/tenant/not-found;
5. verify `Navigation.Resolve` URL works with base path.

---

## Phase 6 — Page image workflow

1. inject image preparation service;
2. add pending state;
3. add InputFile;
4. add bounded preparation;
5. add preview;
6. add state machine;
7. extend dirty state;
8. build image-change command in Save;
9. update Reload Latest / Keep My Changes;
10. update Clone / Copy / Blank;
11. smoke test.

---

## Phase 7 — Professional compact UI

1. change Description maxlength to 200;
2. replace six tall edit cards with four logical groups;
3. add 12-column internal field grid;
4. place image in Item Identity;
5. redesign View mode to same grouping;
6. add responsive CSS;
7. verify dark/light;
8. verify keyboard order.

---

## Phase 8 — Final regression

Run:

```text
full build
full automated tests
Stock Master create/edit/view/copy
all image workflows
all current Stock Master validation
concurrency
delete reference protection
base-path test
responsive test
browser console/server logs
```

---

# 87. Code-Agent Stop Conditions

The AI coding agent must stop and report rather than guess if it discovers:

1. `IvStockMaster.ImagePath` no longer exists;
2. branch HEAD materially changed the save contract;
3. another item-image implementation already exists;
4. the target DB schema is known to lack ImagePath;
5. ImageSharp 4.1.2 cannot restore under the project's package policy;
6. a circular DI dependency appears;
7. current RowVersion behaviour differs materially from the verified service;
8. implementation would require altering costing/posting to make the image work.

Do not silently invent a replacement architecture.

---

# 88. No-Scope-Creep Rules for AI Agent

Do not "improve" unrelated Stock Master semantics while touching the page.

Examples forbidden in this task:

```text
replace GL text boxes with new lookup
change Tax Group validation
change MfgType ownership
change posting defaults
change item-code generation
change UOM rules
change Class/Subclass data model
change price authority
add thumbnails to lists
```

Only the Description length mismatch is explicitly approved because it is already 200 in the current backend.

---

# 89. Definition of Done — Image

- [ ] one primary item image;
- [ ] JPG/PNG/WebP accepted;
- [ ] actual decoded format verified;
- [ ] invalid/unsupported formats rejected;
- [ ] raw upload <=8 MB;
- [ ] input <=25M pixels;
- [ ] max side <=12,000;
- [ ] EXIF auto-orient;
- [ ] metadata stripped;
- [ ] output <=1024×1024 bounding box;
- [ ] smaller input not upscaled;
- [ ] output WebP quality 85;
- [ ] normalized bytes <=4 MB;
- [ ] private storage outside wwwroot;
- [ ] relative path <=500;
- [ ] browser cannot set ImagePath;
- [ ] normal form save preserves existing image;
- [ ] new item + image is one coordinated Save;
- [ ] Add-only user can create with image;
- [ ] edit replacement requires Edit;
- [ ] remove requires Edit through normal Save;
- [ ] stale edit creates no permanent replacement file;
- [ ] old file deleted only after DB success;
- [ ] new file cleaned on DB failure;
- [ ] item delete cleans managed image after DB commit;
- [ ] tenant-isolated authenticated read;
- [ ] unmanaged paths never expose arbitrary server files.

---

# 90. Definition of Done — UI

- [ ] six wasteful tall cards replaced by four logical groups;
- [ ] Item Identity is primary;
- [ ] image visible without dominating page;
- [ ] Description wide and maxlength 200;
- [ ] UOM/size/color/qty/price/etc. use compact cells;
- [ ] no arbitrary tiny fixed editor widths;
- [ ] no horizontal scroll at supported widths;
- [ ] mobile collapses to one column;
- [ ] View mode uses same grouping;
- [ ] Save/Cancel convention unchanged;
- [ ] image uses same Save;
- [ ] Cancel truly discards pending image change;
- [ ] Copy Item does not copy image;
- [ ] keyboard order follows visual order;
- [ ] dark/light both readable;
- [ ] image URL uses `Navigation.Resolve`.

---

# 91. Definition of Done — Regression

- [ ] existing Stock Master tests pass;
- [ ] new image tests pass;
- [ ] solution builds;
- [ ] create works;
- [ ] edit works;
- [ ] view works;
- [ ] copy works;
- [ ] Class → Subclass works;
- [ ] Warehouse → Location works;
- [ ] Stock/Lot/Expiry rules work;
- [ ] Selling GL required remains;
- [ ] Classification required remains;
- [ ] Min <= Max remains;
- [ ] structural-field locks remain;
- [ ] concurrency popup remains;
- [ ] delete reference protection remains;
- [ ] no posting/costing/ledger/month-end code changed;
- [ ] no DevExpress expression runtime error;
- [ ] IIS sub-application image route verified.

---

# 92. Final Architecture Review

This plan is intentionally **not**:

```text
Save item
then independently upload image
```

because that creates:

- permission ambiguity;
- partial-success UX;
- New-mode retry defects;
- extra endpoint attack surface.

The approved design is:

```text
Prepare image
      ↓
normal Stock Master Save
      ↓
business fields + managed ImagePath coordinated by server
```

This is the simplest architecture that is simultaneously:

- secure;
- user-friendly;
- concurrency-safe;
- compatible with Add-only creation rights;
- compatible with the current Stock Master save model;
- easy for an AI coding agent to implement deterministically.

---

# 93. Approval Decision

## **APPROVED FOR IMPLEMENTATION — 10/10**

Professional review result:

```text
Repository alignment        PASS
Current baseline verified   PASS
UI feasibility              PASS
Add/Edit authorization      PASS after revision
New-item save sequencing    PASS after revision
Concurrency design          PASS
File/DB consistency         PASS
Tenant security             PASS
Path traversal protection   PASS
IIS base-path safety        PASS after revision
Blazor Server memory bound  PASS after revision
DevExpress binding safety   PASS
Regression isolation        PASS
Testing completeness        PASS
Scope control               PASS
```

**Final implementation status: APPROVED.**

Use this document as the sole implementation plan for the Stock Master item-image + compact professional UI task on:

```text
mokth/net10projectTemplate
branch: productionv2
verified commit: a552ed1f39bfaa786e1904c5037390521b052586
```

If the branch changes before coding begins, perform Phase 1 again and reconcile the plan with the new HEAD before modifying code.
