# Stock Master Item Image + Professional Compact Entry UI
## AI Code Agent Implementation Plan — `productionv2`

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified baseline commit:** `a552ed1f39bfaa786e1904c5037390521b052586`  
**Baseline commit date:** 2026-10-09  
**Plan status:** **APPROVED FOR IMPLEMENTATION**  
**Implementation readiness score:** **10/10**  
**Scope:** Stock Master Entry only, plus the minimum supporting image service/endpoints/configuration/tests.  
**Important:** This approval applies to this plan against the verified baseline above. If the branch moves materially before implementation, the coding agent must re-check the touched files before editing.

---

# 1. Objective

Improve `IvStockMasterEntry` so that it is:

- professional and visually balanced;
- fast and intuitive for daily ERP data entry;
- compact without becoming cramped;
- responsive on desktop/tablet/mobile;
- able to preview, upload/replace, and remove a single item image;
- safe for multi-tenant use;
- safe for IIS deployment;
- isolated from inventory posting, costing, stock ledger, month-end, and production logic.

The redesign must **not** turn Stock Master into a complicated tab-heavy screen. The goal is fewer wasted wide controls, less vertical scrolling, clearer grouping, and better identification of the item.

---

# 2. Verified Current Repository Facts

The coding agent must treat these as the current baseline facts.

## 2.1 Current Stock Master page

Existing files:

- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css`

The page currently uses shared Inventory UI chrome from:

- `ErpWeb/wwwroot/css/inventory-chrome.css`

The current edit page has six cards:

1. Basic
2. Classification
3. Units
4. Inventory
5. Pricing
6. Accounting

Most cards use:

```text
iv-form-grid iv-form-grid--single
```

which forces one field per full-width row. This is the main source of wasted horizontal space and excess page height.

The shared CSS already provides:

- `.iv-page`
- `.iv-card`
- `.iv-form-grid`
- `.iv-form-grid--single`
- `.iv-field`
- `.iv-field__label`
- `.iv-detail-grid`
- `.iv-footer`
- responsive mobile collapse rules

Do not replace the shared ERP visual language. Extend it locally in `IvStockMasterEntry.razor.css`.

## 2.2 Existing item image database support

The entity already contains:

```csharp
public string? ImagePath { get; set; }
```

in:

- `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`

and the EF configuration already contains:

```csharp
builder.Property(e => e.ImagePath).HasMaxLength(500);
```

in:

- `ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs`

Therefore:

**No new Stock Master image database column is required.**

Do not create a duplicate image field or image table for this scope.

## 2.3 Current application service does not expose image state

The current edit VM:

- `IvStockMasterEditVm`

in:

- `ErpWeb.Core/Inventory/IvMasterResults.cs`

does not expose `ImagePath` or image availability.

The current mappings in:

- `ErpWeb.Core/Inventory/IvStockMasterService.cs`

do not handle image state.

`ApplyEditableFields(...)` currently handles normal Stock Master business fields only.

This plan intentionally keeps image-path mutation **out of** `ApplyEditableFields(...)`.

## 2.4 Existing private attachment architecture

The repository already stores supplier attachments outside `wwwroot`, using:

- `ErpWeb.Core/Purchase/AttachmentStorageOptions.cs`
- `ErpWeb.Core/Purchase/PoSupplierAttachmentService.cs`
- `ErpWeb/Purchase/PoSupplierAttachmentEndpoints.cs`

The existing architecture already demonstrates:

- private filesystem storage;
- tenant-aware access;
- generated physical filenames;
- path-root validation;
- temporary files;
- magic-byte/content validation;
- authorized endpoints;
- best-effort physical cleanup.

The new item-image implementation should follow these principles rather than inventing a public `wwwroot/uploads` folder.

## 2.5 DI and endpoint registration

Core services are registered in:

- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

HTTP endpoints are mapped in:

- `ErpWeb/Program.cs`

The current Stock Master service is registered as:

```csharp
services.AddScoped<IIvStockMasterService, IvStockMasterService>();
```

## 2.6 Current solution

Build target:

- `ErpWeb.slnx`

Target framework:

- .NET 10

Current DevExpress version:

- `26.1.4`

---

# 3. Approved UX Design

## 3.1 Do not use tabs for the main Stock Master fields

Tabs would reduce page height but increase clicks and hide important setup information.

For Stock Master, the preferred design is a compact, visible form with four logical groups.

## 3.2 Final section structure

### Section A — Item Identity

Full-width card.

Layout:

```text
┌──────────────────────────────────────────────────────────────────────┐
│ Item Identity                                                        │
│                                                                      │
│ ┌─────────────────┐  Item Code      [ ITEM001            ]           │
│ │                 │  Description    [ Industrial Motor........... ]  │
│ │   ITEM IMAGE    │                                                    │
│ │                 │  Barcode        [ 9551234567890 ]                 │
│ │   preview       │  Brand          [ Panasonic       ]               │
│ │                 │                                                    │
│ │ Upload/Replace  │  Active         [✓]                              │
│ │ Remove          │  Supply Method  BUY — Purchased / Stock          │
│ └─────────────────┘                                                   │
└──────────────────────────────────────────────────────────────────────┘
```

Rules:

- image panel on the left at desktop widths;
- business identity fields on the right;
- image panel stacks above fields at narrow widths;
- description receives the largest width;
- item code, barcode, and brand use sensible compact widths;
- supply method remains read-only because Production Definition controls it;
- active status remains obvious but does not consume an entire row.

### Section B — Product Classification

Compact responsive grid:

- Type
- Class
- Subclass
- Classification
- Size
- Color

Short fields must not be rendered as page-wide controls.

### Section C — Units & Inventory

Combine the existing Units and Inventory cards because users configure these together.

Group visually into two sub-sections:

**Units**

- Std UOM
- Selling UOM
- Purchase UOM
- Std Pack Size
- Purchase Pack Size

**Inventory control**

- Stock Control
- Lot Control
- Expiry Control
- Default Warehouse
- Default Location
- Min Stock
- Max Stock

Keep existing dependent rules:

- lot control requires stock control;
- expiry control disabled when lot control is disabled;
- location depends on warehouse;
- existing structural-field lock rules must remain untouched.

### Section D — Pricing & Accounting

Combine the existing Pricing and Accounting cards.

Fields:

- Selling Price
- Purchase Price
- Selling GL
- Purchase GL
- Tax Group
- Purchase Tax Group

Use a compact two-/three-column arrangement depending on viewport width.

## 3.3 Field width strategy

Do **not** solve the problem by setting arbitrary pixel widths directly on every DevExpress component.

Use a local responsive grid in `IvStockMasterEntry.razor.css`.

Recommended local class model:

```text
.iv-stock-fields
.iv-stock-field--xs
.iv-stock-field--sm
.iv-stock-field--md
.iv-stock-field--lg
.iv-stock-field--full
```

Desktop example:

- `xs` = 2 of 12 columns
- `sm` = 3 of 12
- `md` = 4 of 12
- `lg` = 6 of 12
- `full` = 12 of 12

At tablet widths, increase spans.

At mobile widths, every field becomes full width.

Use width based on expected business data, not database max length alone.

Examples:

| Field | Suggested relative width |
|---|---|
| Item Code | medium |
| Description | large/full |
| Barcode | medium |
| Brand | medium |
| UOM fields | small |
| Size | small/medium |
| Color | small/medium |
| Min/Max Stock | small |
| Prices | medium |
| GL Codes | medium |
| Tax Groups | medium |
| Warehouse | medium |
| Location | medium |

## 3.4 Keyboard/data-entry behaviour

Preserve normal DOM order matching the visual order so Tab navigation is predictable.

Do not use CSS visual reordering that causes the keyboard focus order to differ from what the user sees.

Do not automatically move focus after selection unless an existing ERP-wide convention already does so.

No popup is required for ordinary short fields.

---

# 4. Approved Item Image Behaviour

## 4.1 One primary image per item

This scope implements exactly one primary image for each Stock Master item.

Do not build an image gallery.

The existing `IvStockMaster.ImagePath` remains the authoritative image reference.

## 4.2 Maximum dimensions

Approved maximum output bounding box:

```text
1024 × 1024 pixels
```

Rules:

- preserve aspect ratio;
- never stretch;
- never crop by default;
- never upscale a smaller source;
- rotate/flip based on EXIF orientation before resizing.

Examples:

| Source | Saved dimensions |
|---|---|
| 3000×2000 | 1024×683 |
| 2000×3000 | 683×1024 |
| 800×600 | 800×600 |
| 500×500 | 500×500 |

Use ImageSharp resize mode equivalent to:

```text
ResizeMode.Max
```

## 4.3 Supported upload formats

Accept only:

- JPEG / JPG
- PNG
- WebP

Reject:

- GIF
- SVG
- BMP
- TIFF
- HEIC/HEIF unless explicitly added in a later feature
- PDFs/documents
- files that merely have an allowed extension but fail actual image decoding

Do not trust filename extension or `Content-Type` alone.

## 4.4 Upload size

Approved raw upload maximum:

```text
8 MB
```

Reject before full processing when the stream exceeds the configured maximum.

Also protect against decompression-bomb-style images:

- reject absurd image dimensions;
- enforce a decoded pixel ceiling, recommended `40,000,000` pixels;
- reject multi-frame/animated image content;
- keep the final normalized output reasonably bounded.

## 4.5 Output encoding

Normalize saved images to:

```text
WebP
```

Recommended quality:

```text
85
```

Rationale:

- good product-photo quality;
- substantially smaller than many phone JPEG/PNG uploads;
- modern browser support;
- consistent storage format;
- predictable image delivery.

If transparent PNG input is supported, ensure the chosen WebP encoding path preserves transparency correctly.

Strip nonessential metadata, especially EXIF GPS/location metadata.

## 4.6 Current package to use

Add to `ErpWeb.Core/ErpWeb.Core.csproj`:

```text
SixLabors.ImageSharp 4.1.2
```

This was the current stable NuGet version verified on 2026-10-09 and targets .NET 8 or higher, therefore it is compatible with this .NET 10 solution.

Do not use `System.Drawing.Common` for server-side image processing.

Before committing the dependency, the coding agent should follow the company's normal package/license approval policy.

---

# 5. Image Storage Design

## 5.1 Private storage

Images must be stored outside `wwwroot`.

Default root:

```text
App_Data/item-images
```

Do not save item images under:

```text
wwwroot/uploads
wwwroot/images/items
```

## 5.2 Add item-image options

Create:

- `ErpWeb.Core/Inventory/ItemImageStorageOptions.cs`

Suggested configuration:

```text
SectionName = "ItemImages"

RootPath = "App_Data/item-images"
MaxUploadBytes = 8 * 1024 * 1024
MaxDimension = 1024
MaxDecodedPixels = 40_000_000
WebpQuality = 85
```

Bind in:

- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

Example configuration section:

```json
"ItemImages": {
  "RootPath": "App_Data/item-images",
  "MaxUploadBytes": 8388608,
  "MaxDimension": 1024,
  "MaxDecodedPixels": 40000000,
  "WebpQuality": 85
}
```

Defaults must still work when the section is omitted.

## 5.3 Physical path

Do not use a raw user-entered item code directly as a filesystem directory without sanitization.

Preferred design:

```text
{RootPath}/{CompanyCode}/{SafeItemKey}/{Guid}.webp
```

`SafeItemKey` should be deterministic and path-safe.

Recommended:

- SHA-256 of normalized item code;
- use a short hex representation sufficient for directory uniqueness.

The relative path saved in `IvStockMaster.ImagePath` must remain under the configured root.

## 5.4 Path validation

Use the same principle already present in `PoSupplierAttachmentService`:

- `Path.GetFullPath(...)`
- verify candidate path begins under normalized root;
- reject any path escaping the root;
- generated filenames only;
- never trust a browser-supplied physical path.

---

# 6. New Core Image Service

Create:

- `ErpWeb.Core/Inventory/IIvStockMasterImageService.cs`
- `ErpWeb.Core/Inventory/IvStockMasterImageService.cs`

The service owns:

- tenant validation;
- access-right validation;
- source-image validation;
- image decoding;
- auto orientation;
- resizing;
- metadata stripping;
- output encoding;
- filesystem writes;
- update/clear of `IvStockMaster.ImagePath`;
- cleanup of replaced/deleted images;
- image read access.

## 6.1 Suggested contracts

The exact names may be adjusted to repository conventions, but the responsibilities must remain.

Example:

```csharp
Task<IvMasterOperationResult<IvPreparedStockImage>> PrepareAsync(
    string originalFileName,
    string? contentType,
    Stream content,
    long contentLength,
    CancellationToken cancellationToken = default);

Task<IvMasterOperationResult<IvStockMasterImageSaveResult>> ReplaceAsync(
    string iCode,
    byte[] expectedRowVersion,
    IvPreparedStockImage preparedImage,
    CancellationToken cancellationToken = default);

Task<IvMasterOperationResult<IvStockMasterImageSaveResult>> RemoveAsync(
    string iCode,
    byte[] expectedRowVersion,
    CancellationToken cancellationToken = default);

Task<IvMasterOperationResult<IvStockMasterImageReadResult>> OpenReadAsync(
    string iCode,
    CancellationToken cancellationToken = default);
```

`IvPreparedStockImage` should contain only normalized server-produced data, for example:

- bytes
- content type (`image/webp`)
- width
- height
- generated extension

It must not contain a browser-provided physical path.

## 6.2 Permissions

`PrepareAsync` may perform pure validation/processing, but final persistent operations must enforce Stock Master permissions server-side.

Required:

- `OpenReadAsync`: Inventory Item Master `Access`
- `ReplaceAsync`: Inventory Item Master `Edit` for existing item
- new-item flow: normal Stock Master Add happens first; once the item exists, image persistence may accept the successful Add flow from the server page, but it must still validate authenticated tenant scope.
- `RemoveAsync`: Inventory Item Master `Edit`

Do not rely only on the UI hiding a button.

## 6.3 Tenant isolation

Every item lookup must include:

```text
CompanyCode + ICode
```

Never fetch by `ICode` globally.

A user from company A must never read or replace company B's item image even when the item codes match.

## 6.4 Concurrency

`ReplaceAsync` and `RemoveAsync` must accept the expected Stock Master `RowVersion`.

Before updating `ImagePath`:

- load the current item in the active company;
- compare row version;
- return `IvMasterErrorCode.Concurrency` on mismatch.

Changing an image is an item modification and should update:

- `ModifiedDate`
- `ModifiedBy`

and naturally advance SQL `RowVersion`.

## 6.5 Safe replace sequence

Approved replace flow:

```text
1. Validate tenant + permission + item + row version
2. Prepared image already validated/normalized
3. Write new bytes to a generated temporary file
4. Verify final path remains under configured root
5. Atomically move temp file to final generated filename
6. Begin/continue DB update
7. Set IvStockMaster.ImagePath = new relative path
8. Update ModifiedDate / ModifiedBy
9. Save DB changes
10. If DB save fails:
      delete the newly written file
      leave old ImagePath untouched
11. After DB success:
      delete old physical image best-effort
12. Return success + newest RowVersion/image state
```

The old file must not be deleted before the DB successfully points to the new image.

Filesystem cleanup failure after a successful DB commit must:

- be logged;
- not make the business save appear failed;
- leave an orphan that can be cleaned later.

## 6.6 Safe remove sequence

Approved remove flow:

```text
1. Validate tenant + Edit permission + item + row version
2. Capture old relative ImagePath
3. Set ImagePath = null
4. Update audit fields
5. Save DB
6. After commit, delete old file best-effort
7. Return newest RowVersion
```

If the physical file is already missing, removal should still succeed after the DB is cleared.

---

# 7. Image Read Endpoint

Create:

- `ErpWeb/Inventory/IvStockMasterImageEndpoints.cs`

Register in:

- `ErpWeb/Program.cs`

Recommended route:

```text
GET /inventory/item-image?iCode={code}
```

This avoids exposing `ImagePath` and avoids route complications if item codes contain unusual characters.

Endpoint requirements:

- authentication required;
- call `IIvStockMasterImageService.OpenReadAsync`;
- tenant-aware;
- Inventory Item Master Access permission;
- return `image/webp`;
- return 404 if the item has no image;
- never expose the server physical path.

Use a cache-busting query from the UI, e.g.:

```text
&v={rowVersionHex}
```

The endpoint may use conservative private caching or `no-store`. Correctness and tenant safety are more important than aggressive caching.

---

# 8. Stock Master View Model Changes

Modify:

- `ErpWeb.Core/Inventory/IvMasterResults.cs`

Add to `IvStockMasterEditVm`:

```csharp
public bool HasImage { get; set; }
```

Do **not** expose the physical/relative `ImagePath` to the editable UI model unless an existing internal contract absolutely requires it.

In:

- `IvStockMasterService.MapEditVm(...)`

set:

```text
HasImage = !string.IsNullOrWhiteSpace(x.ImagePath)
```

## Critical rule

Do **not** add `ImagePath` assignment to:

```text
ApplyEditableFields(...)
```

Normal Stock Master form saving must not accept a client-controlled path.

The dedicated image service is the only authority that changes `IvStockMaster.ImagePath`.

---

# 9. Stock Master Save Flow With Pending Image

Modify:

- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

Add page state similar to:

```text
IvPreparedStockImage? _pendingImage
bool _pendingImageRemoval
string? _pendingImagePreviewUrl/data
bool IsPreparingImage
```

Exact implementation may differ, but behaviour must match this plan.

## 9.1 Selecting an image

When the user selects an image:

1. enforce raw 8 MB stream limit;
2. call image preparation/validation;
3. auto-orient/resize/normalize immediately;
4. keep only the normalized prepared image in the Blazor circuit state;
5. create preview from normalized bytes;
6. do not write a permanent file yet;
7. mark page dirty.

This prevents keeping a huge phone photo in memory for the entire edit session.

## 9.2 New item

Approved new-item flow:

```text
User enters item
        ↓
User selects image
        ↓
Image is validated + normalized in memory
        ↓
User clicks SAVE
        ↓
StockMasters.SaveAsync(Model, isNew: true)
        ↓
If item save fails:
    keep pending image
    show normal validation/error
    write no permanent image
        ↓
If item save succeeds:
    take returned RowVersion
    call image ReplaceAsync(...)
        ↓
If image save succeeds:
    navigate back to item list
        ↓
If image save fails:
    item remains saved
    stay on page
    update Model.RowVersion from successful item save
    show clear warning:
      "Item was saved, but the image could not be saved. Please retry the image."
```

Do not make the user save, return to the list, reopen the item, then upload the image.

## 9.3 Existing item

Image replacement/removal should be committed when the user presses the page's normal `SAVE`.

This gives intuitive Cancel/Discard behaviour:

- selecting a replacement image does not permanently change the item until Save;
- clicking Cancel discards pending image changes;
- Remove marks image removal pending;
- Save commits it.

Do not immediately delete an existing image when the user clicks Remove.

## 9.4 Dirty-state logic

Current dirty logic compares:

```text
Snapshot(Model)
```

Extend it to include pending image state.

Conceptually:

```text
IsDirty =
    form model changed
    OR pending replacement exists
    OR pending removal exists
```

Changing only the item image must enable the normal unsaved-change protection.

## 9.5 Copy Item behaviour

Current copy logic clones the source model, resets item code, barcode and audit data.

New rule:

**Do not copy the source item's image.**

When using `?copy=...`:

- new item's `HasImage = false`;
- no pending image;
- user may upload a new image manually.

This prevents accidentally creating a different SKU with the old SKU's photograph.

---

# 10. Stock Master UI Image Panel

Modify:

- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

## 10.1 Display

Show:

- actual current image;
- pending replacement preview;
- or a neutral placeholder with image icon.

Recommended preview size:

```text
180–220 px square visual area
```

Use:

```text
object-fit: contain
```

Do not crop the product image in the master-entry preview.

## 10.2 Actions

Edit/New mode:

- `UPLOAD IMAGE` when none exists;
- `REPLACE` when one exists;
- `REMOVE` when one exists or a pending replacement exists.

View mode:

- image visible;
- no upload/remove buttons.

Clicking the preview should optionally open a larger image preview popup.

If implemented, keep the popup simple:

- max viewport size;
- object-fit contain;
- Close button;
- no image editing/cropping tools.

## 10.3 File input

Use a Blazor/DevExpress-supported file selection control consistent with the current application.

A hidden native `<InputFile>` / file input triggered by a styled DevExpress button is acceptable and consistent with the supplier attachment pattern.

Do not introduce a large third-party JS upload library.

## 10.4 Feedback

During image preparation:

```text
Processing image…
```

On validation failure, show an inline image-specific error near the image panel.

Examples:

- `Only JPG, PNG, or WebP images are allowed.`
- `Image exceeds the 8 MB upload limit.`
- `Image dimensions are too large.`
- `The selected file is not a valid image.`

Do not dump low-level exception messages to users.

---

# 11. Compact Form CSS Specification

All new layout CSS belongs in:

- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css`

Do not globally modify `inventory-chrome.css` unless a truly reusable fix is proven necessary for multiple pages.

Recommended structure:

```text
.iv-stock-identity
.iv-stock-image-panel
.iv-stock-image-frame
.iv-stock-image
.iv-stock-image-placeholder
.iv-stock-image-actions
.iv-stock-fields
.iv-stock-field--xs
.iv-stock-field--sm
.iv-stock-field--md
.iv-stock-field--lg
.iv-stock-field--full
.iv-stock-subsection
.iv-stock-toggle-row
```

Desktop:

- 12-column internal grid;
- image + fields side-by-side.

Tablet:

- reduce columns/spans naturally;
- keep inputs readable.

Mobile:

- single column;
- image centered/left aligned consistently;
- footer buttons remain usable;
- no horizontal scrolling.

Do not override DevExpress internals more broadly than required.

Keep existing rule that editors fill their allocated grid cell:

```css
.iv-page ::deep .dxbl-text-edit,
.iv-page ::deep .dxbl-combobox,
.iv-page ::deep .dxbl-spin-edit {
    width: 100%;
}
```

The solution is to make the **grid cell** sensible, not make DevExpress controls arbitrarily narrow inside a full-width cell.

---

# 12. View Mode Redesign

The read-only view must also show the image.

Do not leave view mode with the old six-card layout while edit mode uses the new design.

Use the same logical grouping:

1. Item Identity + image
2. Product Classification
3. Units & Inventory
4. Pricing & Accounting
5. Audit

Read-only detail grids can remain compact and two-column where appropriate.

---

# 13. Item Delete Cleanup

Current item delete is handled in:

- `IvStockMasterService.DeleteAsync(...)`

When a Stock Master item is successfully deleted, its image file must be cleaned up.

Do not let physical file deletion participate in the DB transaction in a way that can roll back a valid item deletion.

Approved pattern:

1. capture each item's `ImagePath` before DB removal;
2. perform the existing reference checks and DB delete unchanged;
3. commit DB transaction;
4. delete corresponding image files best-effort;
5. log cleanup failure;
6. do not report item deletion as failed merely because an orphan image could not be removed.

A small cleanup interface may be used to avoid coupling `IvStockMasterService` to all image processing logic, mirroring the supplier attachment cleanup pattern.

Example:

```text
IIvStockMasterImageFileCleanup
```

Register the image service so the same scoped implementation can satisfy both interfaces if appropriate.

---

# 14. Dependency Injection

Modify:

- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

Add:

```text
ItemImageStorageOptions binding
IIvStockMasterImageService -> IvStockMasterImageService
optional IIvStockMasterImageFileCleanup mapping
```

Keep the existing:

```text
IIvStockMasterService -> IvStockMasterService
```

Do not change service lifetimes without a concrete reason.

---

# 15. Endpoint Registration

Modify:

- `ErpWeb/Program.cs`

Add:

```text
app.MapIvStockMasterImageEndpoints();
```

Place with other Inventory endpoint mappings, near:

```text
app.MapIvStockMasterExportEndpoints();
```

The image endpoint must remain behind the application's normal authentication/authorization pipeline.

---

# 16. Files Expected to Change

## Existing files

1. `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
2. `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`
3. `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css`
4. `ErpWeb.Core/Inventory/IvMasterResults.cs`
5. `ErpWeb.Core/Inventory/IvStockMasterService.cs`
6. `ErpWeb.Core/CoreServiceCollectionExtensions.cs`
7. `ErpWeb.Core/ErpWeb.Core.csproj`
8. `ErpWeb/Program.cs`
9. application configuration file only if explicit `ItemImages` values are desired
10. `ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs` where mapping/delete behaviour fits

## New files

Recommended:

1. `ErpWeb.Core/Inventory/ItemImageStorageOptions.cs`
2. `ErpWeb.Core/Inventory/IIvStockMasterImageService.cs`
3. `ErpWeb.Core/Inventory/IvStockMasterImageService.cs`
4. `ErpWeb/Inventory/IvStockMasterImageEndpoints.cs`
5. `ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs`

The agent may adjust test filenames to match the repository's existing test naming convention.

---

# 17. Files/Areas That Must Not Be Changed

Unless compilation proves a direct dependency, do not change:

- inventory posting services;
- stock ledger;
- costing services;
- FIFO/cost layers;
- inventory period close/month-end;
- inventory balances;
- lot allocation logic;
- Sales documents;
- Procurement transaction posting;
- Production Work Order logic;
- Production costing;
- Delivery Request logic;
- UOM conversion rules;
- `IvStockMaster` primary key;
- `IvStockMaster.ImagePath` database definition.

No migration should be necessary for this feature because `ImagePath` already exists.

---

# 18. DevExpress Binding Safety

Follow the repository skill:

- `.agents/skills/devexpress-blazor-editor-binding-safety/skill.md`

For all DevExpress editors:

- prefer normal two-way `@bind-*` where possible;
- when using explicit `Value` + `ValueChanged`, provide the required `ValueExpression`;
- when using `Text` + `TextChanged`, provide `TextExpression`;
- do not introduce another `TextExpression` / `ValueExpression` runtime failure.

Existing examples such as Lot Control and Expiry Control already demonstrate the required pattern.

---

# 19. Image Service Tests

Create focused tests.

Minimum required automated cases:

## Processing

1. JPG larger than 1024×1024 is resized within the 1024 bounding box.
2. Portrait image preserves aspect ratio.
3. Image smaller than 1024 is not upscaled.
4. Invalid/non-image content is rejected.
5. Unsupported format is rejected.
6. Raw input exceeding 8 MB is rejected.
7. Excessive decoded pixel count is rejected.
8. Multi-frame/animated image is rejected.
9. Normalized output is valid WebP.
10. Output dimensions reported by service match actual saved image.

## Security/tenant

11. Company A cannot read Company B's image for the same item code.
12. Company A cannot replace Company B's image.
13. Access permission is required to read.
14. Edit permission is required to replace/remove.
15. Malicious item-code/path input cannot escape storage root.
16. Physical path is never returned to the UI/HTTP response.

## Replace/remove

17. Replace updates `ImagePath`.
18. Replace updates ModifiedDate/ModifiedBy.
19. Replace advances row version.
20. Stale row version returns Concurrency.
21. Successful replacement removes old physical image.
22. DB failure does not delete the old image.
23. DB failure cleans the newly-created replacement file.
24. Remove clears `ImagePath`.
25. Remove remains successful when the old physical file is already missing.

## Item delete

26. Deleting an unused item cleans its item image after DB success.
27. Delete blocked because the item is in use must not delete its image.

---

# 20. Stock Master Service Tests

Extend existing Stock Master tests for:

1. `GetAsync` returns `HasImage = true` when `ImagePath` is populated.
2. `GetAsync` returns `HasImage = false` when no image exists.
3. normal `SaveAsync` does not overwrite/clear an existing `ImagePath`.
4. copied item starts without an inherited image in the UI flow.
5. existing business validation behaviour remains unchanged.

---

# 21. UI Verification Checklist

The coding agent must manually verify these scenarios after implementation.

## New item

- open `/inventory/items/new`;
- form is compact;
- short fields no longer consume an entire card width;
- select valid large photo;
- preview appears;
- dimensions are normalized;
- Save creates item + image;
- reopen item and image displays.

## Invalid image

- choose `.txt` renamed to `.jpg`;
- upload is rejected;
- no physical file remains.

## Replace

- open existing item;
- choose replacement;
- preview changes;
- Cancel;
- reopen item;
- original image still exists.

Then:

- choose replacement;
- Save;
- new image displays;
- old physical image removed.

## Remove

- click Remove;
- image shows pending removal/placeholder;
- Cancel preserves original.

Then:

- Remove;
- Save;
- image no longer exists;
- DB `ImagePath` becomes null.

## Copy

- copy an item that has an image;
- copied New Item screen must not inherit source image;
- save copied item without selecting an image;
- new item has no image.

## Responsive

Verify at approximately:

- 1440px
- 1024px
- 768px
- 390px

No horizontal page scrolling.

## Keyboard

Tab through all fields.

Focus order must match visual order.

---

# 22. Regression Verification

The implementation is not approved until all of these remain correct:

- New item save.
- Edit item save.
- View mode.
- Copy item.
- Class → Subclass dependency.
- Warehouse → Location dependency.
- Stock Control / Lot Control validation.
- Expiry Control behaviour.
- Required Selling GL validation.
- Required Classification validation.
- Min Stock <= Max Stock validation.
- RowVersion concurrency popup.
- Cancel/discard popup.
- item activate/deactivate.
- item delete reference protection.

Image work must not weaken any existing validation or concurrency rule.

---

# 23. Build/Test Commands

From repository root:

```bash
dotnet restore ErpWeb.slnx
dotnet build ErpWeb.slnx
dotnet test ErpWeb.slnx
```

If the full test suite contains unrelated pre-existing failures, the agent must:

1. report them separately;
2. prove the new/modified Stock Master tests pass;
3. not hide new failures behind old failures.

---

# 24. Implementation Sequence for AI Code Agent

Follow this order.

## Phase 1 — Baseline re-check

1. Confirm branch `productionv2`.
2. Record current commit.
3. Re-open all files listed in section 16.
4. Confirm `IvStockMaster.ImagePath` still exists.
5. Confirm no newer item-image implementation already landed.
6. If branch changed, reconcile differences before editing.

## Phase 2 — Core image infrastructure

1. Add ImageSharp 4.1.2 package.
2. Add `ItemImageStorageOptions`.
3. Add image service contracts/results.
4. Implement processing + storage + tenant/security rules.
5. Register service/options.
6. Add image service tests.
7. Build/test.

Do not start the UI redesign until the image service tests are healthy.

## Phase 3 — Read endpoint

1. Add item-image GET endpoint.
2. Map endpoint in `Program.cs`.
3. Test tenant isolation and not-found behaviour.

## Phase 4 — Stock Master VM/service integration

1. Add `HasImage`.
2. Update `MapEditVm`.
3. Confirm `ApplyEditableFields` cannot write image path.
4. Integrate delete cleanup after successful DB commit.
5. Extend Stock Master tests.

## Phase 5 — Page image workflow

1. Add pending-image state.
2. Add file-selection handler.
3. Process selected image before final Save.
4. Add image preview.
5. Add replace/remove behaviour.
6. Extend dirty-state logic.
7. Implement save sequencing.
8. Ensure Copy Item does not carry image.
9. Handle partial success clearly if item save succeeds but image persistence fails.

## Phase 6 — UI redesign

1. Replace six tall cards with four logical groups.
2. Add compact responsive field grid.
3. Add image identity area.
4. Apply same grouping to View mode.
5. Preserve shared Inventory chrome.
6. Validate mobile breakpoints.

## Phase 7 — Final verification

1. build;
2. test;
3. manual UI checklist;
4. check dark/light themes if both are supported;
5. inspect browser console;
6. verify IIS path-base behaviour for image URLs;
7. verify no image physical path leaks;
8. verify no transaction/costing code changed.

---

# 25. Important Failure Handling

## Normal item validation fails

- do not persist pending image;
- keep preview;
- show existing field validation.

## Item save succeeds, image replace fails

Do **not** tell the user the entire save failed.

Show:

```text
Item was saved, but the image could not be saved. Please retry the image.
```

Stay on the edit page.

Update the page model with the newly returned Stock Master RowVersion before the retry.

## Concurrency during image commit

Return normal concurrency handling.

Do not silently overwrite another user's item change.

## Filesystem unavailable

- return friendly image-specific error;
- log technical exception;
- do not expose disk path in the UI;
- preserve the existing item image.

---

# 26. Logging Requirements

Use structured logging for image filesystem failures.

Include safe operational identifiers:

- CompanyCode
- ItemCode
- operation (`prepare`, `replace`, `remove`, `read`, `delete-cleanup`)
- relative image path where appropriate

Do not log:

- full image bytes;
- raw EXIF metadata;
- user filesystem client path;
- secrets.

---

# 27. Performance Requirements

- do not load item image bytes as part of Stock Master `GetAsync`;
- `GetAsync` only returns `HasImage`;
- browser fetches image separately;
- normalize uploaded images once;
- do not resize on every GET;
- output image max 1024×1024;
- raw upload max 8 MB;
- image endpoint streams the stored image;
- no base64 image persisted to the database.

---

# 28. Accessibility / Usability Requirements

- image action buttons must have text or accessible labels;
- placeholder must not be mistaken for a real image;
- image `<img>` must use meaningful alt text, e.g. item description/code;
- keyboard users can activate Upload/Replace/Remove;
- error text associated visually with image area;
- buttons must remain visible in dark/light themes;
- no tiny icon-only destructive action without tooltip/aria label;
- Remove should be clearly destructive but should not immediately delete until Save.

---

# 29. Definition of Done

The task is complete only when all statements below are true.

### Image

- [ ] Stock Master can display an item image.
- [ ] New item can select image before first Save.
- [ ] Existing item can replace image.
- [ ] Existing item can remove image.
- [ ] Copy Item does not copy image.
- [ ] Raw upload limited to 8 MB.
- [ ] JPG/PNG/WebP only.
- [ ] Actual image content validated.
- [ ] EXIF orientation handled.
- [ ] Max saved dimensions 1024×1024.
- [ ] Aspect ratio preserved.
- [ ] Smaller images are not upscaled.
- [ ] Saved output normalized to WebP.
- [ ] Files stored outside `wwwroot`.
- [ ] `ImagePath` stores only server-controlled relative path.
- [ ] tenant isolation verified.
- [ ] row-version concurrency enforced.
- [ ] replaced/deleted image cleanup implemented.
- [ ] no new DB image column/migration introduced.

### UI

- [ ] Full-width one-field-per-row layout removed where inappropriate.
- [ ] Item Identity becomes clear primary section.
- [ ] image displayed prominently but not excessively large.
- [ ] Classification compact.
- [ ] Units + Inventory logically combined.
- [ ] Pricing + Accounting logically combined.
- [ ] Description remains wide.
- [ ] short inputs remain compact.
- [ ] form remains readable at 1024px desktop/laptop width.
- [ ] mobile layout collapses cleanly.
- [ ] keyboard tab order remains logical.
- [ ] existing footer Save/Cancel convention preserved.
- [ ] View mode matches new information architecture.

### Regression

- [ ] Stock Master existing tests pass.
- [ ] new image tests pass.
- [ ] solution builds.
- [ ] no inventory posting/costing/ledger/month-end code changed.
- [ ] existing Stock Master validation remains intact.
- [ ] existing concurrency behaviour remains intact.
- [ ] no DevExpress binding-expression runtime errors introduced.

---

# 30. Explicit Non-Goals

Do not implement in this task:

- multiple images/gallery;
- drag-to-reorder images;
- crop editor;
- AI background removal;
- camera capture;
- image zoom library;
- image CDN;
- Azure Blob/S3 storage abstraction;
- e-commerce image publishing;
- Stock Master List thumbnails;
- sales document item thumbnails;
- barcode scanning changes;
- new costing behaviour;
- new Stock Master fields unrelated to layout/image.

These can be separate enhancements after this implementation is stable.

---

# 31. Approval Decision

## APPROVED FOR IMPLEMENTATION

This plan is approved because:

1. it uses the **existing `IvStockMaster.ImagePath`** instead of creating redundant schema;
2. it follows the repository's established **private attachment/storage security pattern**;
3. it keeps the normal Stock Master save contract free from client-controlled filesystem paths;
4. it preserves current inventory validation and row-version concurrency;
5. it isolates image handling from posting, costing, ledger and month-end;
6. it directly fixes the current UI problem caused by full-width single-field rows;
7. it provides a compact responsive design without hiding important fields behind tabs;
8. it handles new-item image selection without forcing an awkward save/reopen workflow;
9. it defines failure and cleanup behaviour rather than leaving partial file/DB states unspecified;
10. it includes concrete automated and manual verification criteria.

**Final readiness:** **10/10 — APPROVED FOR IMPLEMENTATION on verified `productionv2` baseline `a552ed1f39bfaa786e1904c5037390521b052586`.**
