# Stock Master Multi-Image Gallery Enhancement
## AI Code Agent Implementation Plan — `productionv2`

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Connected GitHub baseline currently visible:** `a552ed1f39bfaa786e1904c5037390521b052586`  
**Plan status:** **APPROVED FOR IMPLEMENTATION**  
**Professional review status:** **PASSED**  
**Implementation readiness:** **10/10 after mandatory Phase 0 reconciliation**

> **Repository visibility note**
>
> The connected GitHub `productionv2` branch still reports commit
> `a552ed1f39bfaa786e1904c5037390521b052586`, and that connected tree does not
> yet expose the user's completed single-image implementation.
>
> The AI coding agent must therefore begin by tracing the actual working tree/branch
> that contains the implemented Stock Master image feature. Do not create duplicate
> image services, options, endpoints, or processing pipelines if they already exist.

---

# 1. Goal

Enhance Stock Master from:

```text
1 item -> 1 image
```

to:

```text
1 item
   -> 1 PRIMARY image
   -> up to 5 ADDITIONAL images
```

Approved default:

```text
Maximum images per item = 6
```

This is intentionally practical for SME ERP use. Most items may still have one image.

Do **not** turn Stock Master into a media-management system.

---

# 2. Scope

## In scope

- one primary image plus additional item images;
- max 6 images/item;
- thumbnail strip;
- large selected-image preview;
- Add Images;
- Remove;
- Set Primary;
- multi-file selection;
- single normal Stock Master Save;
- migration/backfill from the current single image;
- tenant isolation;
- RowVersion concurrency;
- safe multi-file cleanup;
- responsive UI;
- IIS base-path safety;
- automated regression tests.

## Explicitly out of scope

Do not implement:

- unlimited image gallery;
- video;
- PDF/document attachment;
- captions as required fields;
- drag reorder;
- crop editor;
- AI background removal;
- image history/versioning;
- e-commerce publishing;
- CDN;
- Azure Blob/S3 abstraction;
- image approval workflow;
- thumbnails in Sales/PO/Production;
- costing/posting changes.

---

# 3. Backward-Compatibility Rule

The existing single-image field:

```text
IvStockMaster.ImagePath
```

must remain.

It becomes the **canonical PRIMARY image pointer**.

Existing downstream modules may continue reading only:

```text
IvStockMaster.ImagePath
```

and therefore require no change.

This enhancement must not require changes to:

- Sales;
- Delivery Request;
- Procurement;
- Inventory transactions;
- Work Order;
- Production;
- costing;
- stock ledger;
- period close/month end.

---

# 4. Data Model — Approved Design

Create a new child table:

```text
IvStockMasterImage
```

containing every gallery image, including the primary image.

Do **not** add an `IsPrimary` column.

Canonical design:

```text
IvStockMasterImage = gallery membership
IvStockMaster.ImagePath = primary pointer
```

An image is primary when:

```text
IvStockMasterImage.ImagePath == IvStockMaster.ImagePath
```

This avoids two independent primary flags drifting out of sync.

---

# 5. Data Invariants

For an item with no images:

```text
IvStockMasterImage count = 0
IvStockMaster.ImagePath = NULL
```

For an item with images:

```text
IvStockMasterImage count = 1..6
IvStockMaster.ImagePath is NOT NULL
IvStockMaster.ImagePath exactly matches one gallery row
```

Additional rules:

```text
same item cannot contain same ImagePath twice
parent RowVersion gates all gallery mutations
browser never sends or receives physical ImagePath
```

---

# 6. New Entity

Create:

```text
ErpWeb.Model/Entities/Inventory/IvStockMasterImage.cs
```

Recommended:

```csharp
public sealed class IvStockMasterImage
{
    public long Uid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;

    public string ImagePath { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }

    public IvStockMaster? Item { get; set; }
}
```

Do not add in this release:

```text
IsPrimary
Caption
ModifiedDate
ModifiedBy
RowVersion
```

Parent Stock Master owns audit/concurrency for gallery changes.

---

# 7. EF Configuration

Create:

```text
ErpWeb.Model/Configurations/Inventory/IvStockMasterImageConfiguration.cs
```

Map:

```text
UID         bigint identity PK
CompanyCode nvarchar(5)   NOT NULL
ICode       nvarchar(30)  NOT NULL
ImagePath   nvarchar(500) NOT NULL
SortOrder   int           NOT NULL
CreatedDate nullable
CreatedBy   nvarchar(10) nullable
```

Indexes:

```text
UNIQUE (CompanyCode, ICode, ImagePath)
UNIQUE (CompanyCode, ICode, SortOrder)
INDEX  (CompanyCode, ICode)
```

Foreign key:

```text
(CompanyCode, ICode)
  -> IvStockMaster(CompanyCode, ICode)
  ON DELETE CASCADE
```

Cascade is appropriate because child image metadata has no independent business meaning.

Physical files are still cleaned by application code after successful DB commit.

---

# 8. Model Registration

Modify:

```text
ErpWeb.Model/Data/AppDbContext.cs
```

Add:

```csharp
public DbSet<IvStockMasterImage> IvStockMasterImages =>
    Set<IvStockMasterImage>();
```

Modify `IvStockMaster`:

```csharp
public ICollection<IvStockMasterImage> Images { get; set; }
    = new List<IvStockMasterImage>();
```

Do not change the Stock Master primary key.

---

# 9. SQL Deployment Script

Create:

```text
scripts/alter-ivstockmaster-multi-image.sql
```

Follow the repository's existing idempotent deployment-script style.

Do not auto-create/alter the table at application startup.

---

# 10. Existing Single-Image Backfill

When the child table is first created, insert one child row for every current nonblank:

```text
IvStockMaster.ImagePath
```

Concept:

```sql
INSERT INTO dbo.IvStockMasterImage
(
    CompanyCode,
    ICode,
    ImagePath,
    SortOrder,
    CreatedDate,
    CreatedBy
)
SELECT
    CompanyCode,
    ICode,
    ImagePath,
    1,
    GETDATE(),
    'MIGRATION'
FROM dbo.IvStockMaster
WHERE NULLIF(LTRIM(RTRIM(ImagePath)), '') IS NOT NULL;
```

Keep the existing parent `ImagePath` unchanged.

No physical file is moved.

Backfill must run only on first table creation and must not duplicate rows on script rerun.

---

# 11. Deployment Verification

After schema script:

```sql
SELECT m.CompanyCode, m.ICode, m.ImagePath
FROM dbo.IvStockMaster m
WHERE NULLIF(LTRIM(RTRIM(m.ImagePath)), '') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.IvStockMasterImage i
      WHERE i.CompanyCode = m.CompanyCode
        AND i.ICode = m.ICode
        AND i.ImagePath = m.ImagePath
  );
```

Expected:

```text
0 rows
```

Also verify:

- no item has more than 6 rows;
- no duplicate path per item;
- existing physical image files remain untouched.

---

# 12. Reuse the Implemented Image Pipeline

Do not create a second processor.

Reuse the implemented single-image logic for:

- raw size limit;
- JPG/PNG/WebP validation;
- ImageSharp identification/decode;
- EXIF orientation;
- metadata stripping;
- 1024x1024 max bounding box;
- aspect-ratio preservation;
- WebP normalization;
- managed private storage;
- storage-root/path validation.

If class names differ, Phase 0 maps the actual implementation.

---

# 13. Maximum Image Configuration

Extend the existing item-image options:

```csharp
public int MaxImagesPerItem { get; set; } = 6;
```

Startup validation:

```text
1 <= MaxImagesPerItem <= 10
```

Production/default acceptance target:

```text
6
```

Server validation is authoritative.

---

# 14. Blazor Server Memory Guard

Multiple selected images can multiply circuit memory.

Add:

```csharp
public long MaxPendingGalleryBytes { get; set; }
```

Recommended default:

```text
12 MB
```

This limits the **sum of normalized pending images**, not raw browser files.

Process multiple files sequentially, never decode six source images concurrently.

Do not increase global SignalR limits.

---

# 15. Gallery DTO

Add a small metadata DTO:

```csharp
public sealed class IvStockMasterImageRow
{
    public long Uid { get; init; }
    public int SortOrder { get; init; }
    public bool IsPrimary { get; init; }
}
```

`IsPrimary` is calculated only:

```text
child.ImagePath == parent.ImagePath
```

Do not return `ImagePath` to the UI.

---

# 16. Gallery List Service

Extend the implemented image service:

```csharp
Task<IvMasterOperationResult<IReadOnlyList<IvStockMasterImageRow>>> ListAsync(
    string itemCode,
    CancellationToken cancellationToken = default);
```

Requirements:

- tenant validation;
- Inventory Item Master ACCESS permission;
- CompanyCode + ICode scope;
- order by SortOrder;
- computed `IsPrimary`;
- no physical paths exposed.

Maximum six rows makes pagination unnecessary.

---

# 17. Image Read Endpoint

Keep the existing primary-image endpoint behavior.

Enhance it to allow optional image ID:

```text
GET /inventory/item-image?iCode=ITEM001
    -> primary

GET /inventory/item-image?iCode=ITEM001&imageId=123
    -> selected gallery image
```

For `imageId`:

- validate current company;
- validate item code;
- validate child Uid belongs to that same company/item;
- validate managed path;
- stream the image;
- never load by image ID alone.

Use:

```csharp
Navigation.Resolve(...)
```

for every browser-facing URL so `/erpweb` IIS deployments remain correct.

---

# 18. Primary Image Behavior

`IvStockMaster.ImagePath` remains canonical.

Set Primary:

```text
parent.ImagePath = selected child.ImagePath
```

Do not:

- copy image;
- move file;
- re-encode image;
- create/delete child row.

Set Primary must update parent:

```text
ModifiedDate
ModifiedBy
RowVersion
```

through the normal Stock Master Save.

---

# 19. Automatic Primary Rules

## First image

If gallery was empty:

```text
first successfully prepared image becomes primary
```

unless user selects another pending image before Save.

## Primary removed

If primary is removed and images remain:

```text
lowest SortOrder remaining image becomes primary
```

unless user explicitly chooses another remaining/pending image.

## All removed

```text
IvStockMaster.ImagePath = NULL
```

---

# 20. No Captions / No Reorder

Do not require labels such as:

```text
Front
Back
Label
Packaging
```

Visual thumbnails are enough for this release.

Do not implement drag ordering.

`SortOrder` represents upload/add order.

New image:

```text
MAX(SortOrder) + 1
```

Do not renumber after removal.

Example:

```text
1,2,3
remove 2
=> 1,3
add new
=> 1,3,4
```

UI sorts ascending.

---

# 21. Gallery Change Set

Evolve the single-image change command into a gallery-aware change set.

Recommended:

```csharp
public sealed class IvStockMasterImageGalleryChangeSet
{
    public IReadOnlyList<IvPendingStockImage> Additions { get; init; }
        = Array.Empty<IvPendingStockImage>();

    public IReadOnlyList<long> RemoveImageIds { get; init; }
        = Array.Empty<long>();

    public IvStockMasterPrimarySelection? PrimarySelection { get; init; }
}

public sealed class IvPendingStockImage
{
    public Guid Token { get; init; }
    public required IvPreparedStockImage Image { get; init; }
}

public sealed class IvStockMasterPrimarySelection
{
    public long? ExistingImageId { get; init; }
    public Guid? PendingImageToken { get; init; }
}
```

Exact naming may follow the implemented codebase.

---

# 22. Change-Set Validation

Before any permanent file write:

```text
existing count
- distinct valid removals
+ pending additions
<= MaxImagesPerItem
```

Reject:

- remove ID not belonging to this company/item;
- primary existing ID not belonging to item;
- primary ID also removed;
- pending primary token not in Additions;
- both ExistingImageId and PendingImageToken set;
- final count > maximum.

Do not partially accept only some additions during Save.

---

# 23. Concurrency Rule

The parent:

```text
IvStockMaster.RowVersion
```

is the aggregate concurrency token.

For existing item, validate RowVersion **before** storing any new permanent image file.

Order:

```text
normal validation
EDIT permission
load parent
RowVersion validation
load gallery
validate change set
validate count
only then store additions
```

A stale user must create no permanent new file and delete no old file.

---

# 24. Permission Model

New item:

```text
ADD permission
```

covers saving the item and up to six images.

Do not require EDIT immediately after ADD.

Existing item:

```text
EDIT permission
```

required for:

- Add Images;
- Remove;
- Set Primary.

View:

```text
ACCESS
```

required for gallery display.

Server enforcement is mandatory.

---

# 25. Save Contract

Target semantic Save:

```csharp
SaveAsync(
    IvStockMasterEditVm model,
    bool isNew,
    IvStockMasterImageGalleryChangeSet? imageChanges,
    CancellationToken cancellationToken = default)
```

Preserve existing no-image callers.

If the implemented single-image code already added an overload:

- evolve/adapt it;
- search every call site;
- do not leave two competing write mechanisms.

---

# 26. New Item Transaction

Required sequence:

```text
1. Validate Stock Master
2. Validate ADD permission
3. Validate normal business rules
4. Validate duplicate item code
5. Validate gallery change set/count
6. Store every pending prepared image
7. If any store fails:
       clean every newly stored file
       do not insert item
8. Begin/use DB transaction
9. Insert IvStockMaster
10. Insert image child rows
11. assign SortOrder
12. resolve primary
13. set parent.ImagePath
14. SaveChanges
15. Commit
16. if DB fails:
       rollback
       clean every newly stored file
17. return latest model + RowVersion
```

No partial gallery.

---

# 27. Existing Item Transaction

Required:

```text
1. Validate Stock Master edit
2. Validate EDIT permission
3. Validate RowVersion
4. Validate structural field rules
5. Load gallery
6. Validate change set/count
7. Store new pending images
8. if any store fails:
       clean all newly stored files
       no DB change
9. begin/use transaction
10. apply normal Stock Master fields
11. insert new image rows
12. delete requested child rows
13. resolve final primary path
14. set parent.ImagePath
15. stamp ModifiedDate/ModifiedBy
16. SaveChanges
17. Commit
18. after commit:
       delete removed physical files best-effort
19. return latest RowVersion/gallery state
```

---

# 28. File/DB Ordering Invariant

For additions:

```text
new file first
DB reference second
```

For removals:

```text
DB reference removed first
commit
physical delete afterward
```

Never delete an old physical file before DB commit.

---

# 29. Multi-File Failure Cleanup

Example:

```text
A stored
B stored
C fails
```

Required:

```text
delete A
delete B
DB unchanged
old gallery unchanged
old primary unchanged
```

Track all newly-created paths during the operation.

---

# 30. Set Primary Only

If the only gallery change is Set Primary:

```text
no decode
no file write
no file delete
only update parent.ImagePath + parent audit
```

This operation should be cheap.

---

# 31. Stock Master Delete

Before deleting parent:

```text
capture all child ImagePath values
```

Also include parent `ImagePath` defensively if it is not already represented.

Then preserve existing:

- permission checks;
- RowVersion;
- reference/in-use checks.

On successful DB delete:

```text
child metadata cascades
commit
then delete all captured physical files best-effort
```

If delete is blocked or rolls back:

```text
no physical image deletion
```

---

# 32. Gallery Drift Guard

If service finds:

```text
parent.ImagePath != null
but no child row matches
```

do not silently change/delete data.

Recommended:

```text
log invariant warning
return controlled data-integrity/validation error for gallery mutation
```

The normal repair is the deployment/backfill script.

Do not auto-invent gallery rows on every Save.

---

# 33. UI Design

Keep the implemented compact Stock Master layout.

Inside the existing Item Identity/image area:

```text
┌──────────────────────────────────────────────┐
│ ITEM IMAGE                                   │
│                                              │
│        ┌──────────────────────────┐          │
│        │      LARGE PREVIEW       │          │
│        └──────────────────────────┘          │
│                                              │
│ [★1] [2] [3] [4] [5] [6]   [+ ADD IMAGES] │
│                                              │
│ [SET PRIMARY] [REMOVE]         4 / 6 images │
└──────────────────────────────────────────────┘
```

Do not display six large images simultaneously.

---

# 34. Thumbnail Rules

Thumbnail:

- square frame;
- `object-fit: contain`;
- selected border/state;
- Primary star/badge;
- keyboard focusable;
- accessible label.

Clicking thumbnail changes the large preview only.

It does **not** automatically become primary.

---

# 35. Add Images UX

Use:

```text
ADD IMAGES
```

and:

```razor
<InputFile multiple ... />
```

Calculate remaining slots:

```text
MaxImagesPerItem - desired gallery count
```

If user selects too many:

```text
"Maximum 6 images per item. You can add 2 more."
```

Do not silently ignore extra selections.

Process selected files sequentially.

---

# 36. First Image UX

If item has no images:

```text
first successfully prepared image is Primary automatically
```

Additional selected files are normal gallery images.

No popup.

---

# 37. Set Primary UX

Select a non-primary thumbnail:

```text
SET PRIMARY
```

moves the Primary indicator immediately in pending UI state.

DB is not updated until main:

```text
SAVE
```

Cancel restores persisted primary.

---

# 38. Remove UX

## Existing image

Remove from desired UI list and add Uid to pending-removal set.

Do not delete file yet.

## Pending new image

Remove it from pending memory.

No DB/file deletion necessary.

If removed image was primary:

```text
automatically select lowest SortOrder remaining
```

If no images remain:

```text
desired primary = none
```

---

# 39. Dirty State

Page is dirty when:

```text
normal fields changed
OR pending additions exist
OR pending removals exist
OR desired primary differs from persisted primary
```

Changing only Primary must trigger the existing discard confirmation.

---

# 40. Save/Cancel

Keep one normal:

```text
SAVE
```

Do not add:

```text
SAVE GALLERY
UPLOAD NOW
COMMIT IMAGES
```

Cancel/discard:

- clears pending prepared images;
- restores no DB state because nothing was committed;
- leaves persisted files untouched.

---

# 41. View Mode

View mode:

- shows large primary by default;
- shows thumbnails;
- clicking thumbnail changes large preview;
- no Add;
- no Remove;
- no Set Primary.

This is useful as item inquiry without exposing edit actions.

---

# 42. Copy Item

Do not copy images.

Copy flow:

```text
new copied item
gallery = empty
ImagePath = null
```

Source gallery remains unchanged.

---

# 43. No Separate Thumbnail Files

Do not generate:

```text
thumb.webp
medium.webp
large.webp
```

per image.

The existing normalized <=1024 image is enough for a six-image ERP gallery.

CSS displays it at thumbnail size.

---

# 44. Cache / Security

Reuse the implemented endpoint's safe cache/security behavior.

Preferred:

```text
Cache-Control: private, no-store
X-Content-Type-Options: nosniff
```

No gallery image path may be exposed to the browser.

---

# 45. Audit

Child additions store:

```text
CreatedDate
CreatedBy
```

Every gallery mutation updates parent:

```text
ModifiedDate
ModifiedBy
```

No image-history table in this scope.

---

# 46. DevExpress Safety

Re-audit the entire modified Stock Master form using:

```text
.agents/skills/devexpress-blazor-editor-binding-safety/skill.md
```

Do not introduce:

- missing TextExpression;
- missing ValueExpression;
- missing CheckedExpression;
- `@bind-*` plus duplicate `*Changed`;
- disabled validation as a workaround.

`InputFile` itself is not a DevExpress editor and does not use those expression rules.

---

# 47. Compact UI Must Not Regress

Do not reintroduce tall one-field-per-row cards.

Preserve:

- compact field layout;
- Item Identity grouping;
- responsive layout;
- existing Save/Cancel footer;
- readable Description;
- mobile friendliness.

Gallery should fit within the image identity region.

---

# 48. Expected Files

Because the connected repo does not yet expose the implemented single-image code, Phase 0 must map actual names.

Expected model changes:

```text
IvStockMaster.cs
NEW IvStockMasterImage.cs
NEW IvStockMasterImageConfiguration.cs
AppDbContext.cs
```

Expected core changes:

```text
existing ItemImageStorageOptions
existing IIvStockMasterImageService
existing IvStockMasterImageService
existing prepared-image/result types
IIvStockMasterService
IvStockMasterService
IvMasterResults/edit VM types
CoreServiceCollectionExtensions
```

Expected web/UI changes:

```text
existing IvStockMasterImageEndpoints
Program.cs
IvStockMasterEntry.razor
IvStockMasterEntry.razor.cs
IvStockMasterEntry.razor.css
```

Expected SQL:

```text
NEW scripts/alter-ivstockmaster-multi-image.sql
```

Expected tests:

```text
IvStockMasterServiceTests
existing item-image service tests
new gallery tests
```

Do not duplicate existing implemented types because names differ.

---

# 49. Phase 0 — Mandatory Reconciliation

Before coding:

1. locate actual working tree containing the single-image implementation;
2. record current commit if committed;
3. search:
   ```text
   ImagePath
   StockMasterImage
   ItemImageStorageOptions
   PrepareAsync
   IvPreparedStockImage
   HasImage
   item-image endpoint
   ```
4. identify current image processor/storage/read/save flow;
5. identify current page pending-image state;
6. identify tests;
7. map current names to this plan;
8. remove no existing behavior unless explicitly superseded here.

If single-image implementation is absent from the actual working tree:

```text
STOP
```

and report that this enhancement cannot safely be layered until the base feature is present.

---

# 50. Implementation Phases

## Phase 1 — Schema

- add child entity/configuration/DbSet/navigation;
- write idempotent SQL script;
- backfill existing primary images;
- verify invariants.

## Phase 2 — Read support

- gallery DTO;
- `ListAsync`;
- optional `imageId` GET;
- tenant/access/base-path tests.

## Phase 3 — Change set

- additions;
- removals;
- primary selection;
- max-count validation;
- non-breaking Save contract.

## Phase 4 — Transactional save

- new-item multi-image;
- edit additions;
- removals;
- set primary;
- multi-file rollback;
- post-commit cleanup.

## Phase 5 — Delete

- capture all image paths;
- retain current reference checks;
- DB cascade;
- physical cleanup after commit.

## Phase 6 — UI

- large preview;
- thumbnail strip;
- image count;
- multi-select Add Images;
- sequential preparation;
- memory guard;
- Set Primary;
- Remove;
- dirty state;
- one Save;
- View mode.

## Phase 7 — regression

- responsive;
- dark/light;
- keyboard;
- IIS `/erpweb`;
- full build/tests.

---

# 51. Required Automated Tests — Mapping/Schema

1. multiple image rows per item;
2. tenant/item FK correct;
3. duplicate path per item rejected;
4. duplicate SortOrder per item rejected;
5. another company can use same ICode independently;
6. parent delete cascades metadata.

---

# 52. Required Automated Tests — Backward Compatibility

1. existing single ImagePath backfills one child row;
2. parent ImagePath unchanged;
3. no physical file move;
4. old primary endpoint still works;
5. no-image item stays zero rows.

---

# 53. Required Automated Tests — Gallery Read

1. ordered rows returned;
2. ImagePath not exposed;
3. correct IsPrimary calculation;
4. non-primary image can be read by imageId;
5. other item imageId rejected;
6. other company imageId rejected;
7. primary GET without imageId still works.

---

# 54. Required Automated Tests — Count

1. 5 existing + 1 add = success;
2. 6 existing + 1 add = Validation;
3. 6 - 1 remove + 1 add = success;
4. attempted final 7 rejects entire Save;
5. count validation occurs before permanent file write.

---

# 55. Required Automated Tests — New Item

1. ADD-only user can create with one image;
2. ADD-only user can create with multiple images;
3. first image auto-primary;
4. selected pending primary wins;
5. parent ImagePath matches a child row;
6. any file-store failure => no item inserted;
7. DB failure => all newly stored files cleaned.

---

# 56. Required Automated Tests — Existing Add

1. add one additional image;
2. add several;
3. existing primary remains by default;
4. newly-added image may become primary in same Save;
5. parent Modified audit updates;
6. stale RowVersion writes no files.

---

# 57. Required Automated Tests — Remove

1. remove non-primary;
2. remove primary => auto-promote;
3. explicit new primary overrides auto-promotion;
4. remove all => parent ImagePath null;
5. physical delete only after DB success;
6. DB failure preserves old files;
7. missing physical old file does not fail business Save.

---

# 58. Required Automated Tests — Primary Only

1. set existing non-primary as primary;
2. no child insert/delete;
3. no physical file work;
4. parent ImagePath changes;
5. parent audit updates;
6. stale RowVersion rejected.

---

# 59. Required Automated Tests — Multi-File Failure

Scenario:

```text
A stored
B stored
C fails
```

Expected:

```text
A cleaned
B cleaned
DB unchanged
old gallery unchanged
old primary unchanged
```

---

# 60. Required Automated Tests — Delete

1. successful item delete captures all child paths;
2. referenced item delete does not clean files;
3. stale delete does not clean files;
4. successful delete removes metadata;
5. physical cleanup attempted after commit;
6. cleanup failure does not turn DB success into failure.

---

# 61. Manual UI Acceptance — Existing Single Image

After backfill:

- open existing item;
- one thumbnail;
- Primary badge;
- count `1 / 6`;
- large image visible;
- Save with no changes produces no gallery mutation.

---

# 62. Manual UI Acceptance — Add Several

- start with one image;
- Add Images;
- select three;
- processing is sequential;
- four thumbnails shown;
- count `4 / 6`;
- existing primary remains;
- Save;
- reopen;
- all four images available.

---

# 63. Manual UI Acceptance — Maximum

At six images:

- Add disabled or maximum clearly shown;
- selecting too many gives friendly message;
- no silent partial acceptance.

---

# 64. Manual UI Acceptance — Set Primary

- select additional image;
- Set Primary;
- star moves in pending UI;
- Cancel => old primary remains;
- repeat + Save;
- reopen => new primary persists;
- any old single-image consumer now sees new primary automatically.

---

# 65. Manual UI Acceptance — Remove Primary

- remove current primary;
- next lowest SortOrder becomes pending primary;
- Save;
- reopen;
- parent ImagePath points to promoted child;
- removed physical file cleaned.

---

# 66. Manual UI Acceptance — Remove All

- remove all;
- placeholder shown;
- count `0 / 6`;
- Save;
- reopen;
- no gallery;
- parent ImagePath NULL.

---

# 67. Manual UI Acceptance — Copy

- source has multiple images;
- Copy Item;
- new item shows `0 / 6`;
- source images untouched;
- new item may upload its own images.

---

# 68. Manual UI Acceptance — Concurrency

Two users edit same item.

User A saves gallery change.

User B attempts add/remove/set-primary with stale RowVersion.

Required:

```text
Concurrency conflict
no new permanent file from B
no old file deleted by B
pending local UI state remains for normal reload/keep workflow
```

---

# 69. Manual UI Acceptance — Add-Only User

Permissions:

```text
ACCESS yes
ADD yes
EDIT no
```

Required:

- New item may add up to six images.
- New item + gallery saves successfully.
- User cannot later modify existing gallery without EDIT.

---

# 70. Manual UI Acceptance — Responsive

Verify approximately:

```text
1440
1200
1024
768
390 px
```

Requirements:

- no page horizontal scroll;
- thumbnails remain usable;
- mobile thumbnail strip may scroll inside its own region;
- image panel does not dominate form;
- Save/Cancel reachable.

---

# 71. Manual UI Acceptance — IIS Base Path

Under:

```text
/erpweb
```

verify:

- primary image;
- all thumbnails;
- selected preview;
- imageId GET;
- no request escapes to origin-root `/inventory/...`.

---

# 72. Build/Test Gate

Run:

```bash
dotnet restore ErpWeb.slnx
dotnet build ErpWeb.slnx
dotnet test ErpWeb.slnx
```

Do not report completion with compile/test failures introduced by this change.

If unrelated failures pre-exist, report them separately and prove all new/changed tests pass.

---

# 73. Deployment Gate

Before customer deployment:

1. DB backup according to normal process;
2. run `alter-ivstockmaster-multi-image.sql`;
3. run invariant verification;
4. confirm zero unmatched primary paths;
5. deploy application;
6. smoke-test an existing one-image item;
7. add additional image;
8. set new primary;
9. remove an image.

Do not rely on application startup to alter schema.

---

# 74. No-Scope-Creep Rules

Do not change:

- pricing;
- GL logic;
- tax logic;
- MfgType;
- item code generation;
- UOM conversion;
- inventory posting;
- costing;
- FIFO;
- stock ledger;
- month end;
- Sales posting;
- Procurement posting;
- Production posting;
- Work Order;
- Delivery Request;
- e-Invoice.

This is master-data presentation only.

---

# 75. Hard Implementation Invariants

The agent must preserve all of these:

1. `IvStockMaster.ImagePath` remains primary pointer.
2. No persisted child `IsPrimary`.
3. All gallery images have child rows.
4. Max image count enforced server-side.
5. Parent RowVersion gates gallery changes.
6. Stale edit writes no new permanent file.
7. Removed files are physically deleted only after DB commit.
8. Browser never supplies/receives physical image path.
9. Copy Item does not copy images.
10. Downstream modules continue using the parent primary image unchanged.

---

# 76. AI Agent Stop Conditions

Stop and report rather than guess if:

- implemented single-image code cannot be found;
- actual implementation uses a materially different primary-image contract;
- `ImagePath` was removed/renamed;
- image files are not managed private files;
- another gallery implementation already exists;
- schema lengths differ materially;
- child table FK cannot be created due unresolved data;
- gallery enhancement would require changing posting/costing.

Do not create a competing image subsystem.

---

# 77. Definition of Done

## Data

- [ ] child table exists;
- [ ] existing single images backfilled;
- [ ] parent ImagePath retained;
- [ ] no child IsPrimary column;
- [ ] primary pointer always matches a child when images exist;
- [ ] zero images => parent ImagePath null;
- [ ] max count enforced;
- [ ] tenant/item FK correct.

## Service

- [ ] current processor reused;
- [ ] gallery list;
- [ ] additional image read;
- [ ] add/remove/set-primary;
- [ ] first-image auto-primary;
- [ ] removed-primary promotion;
- [ ] RowVersion checked before file writes;
- [ ] ADD-only new gallery works;
- [ ] EDIT required for existing gallery changes;
- [ ] multi-file rollback cleanup;
- [ ] DB-failure cleanup;
- [ ] post-commit old-file cleanup;
- [ ] parent-delete cleanup;
- [ ] no physical path exposure.

## UI

- [ ] large preview;
- [ ] thumbnail strip;
- [ ] Primary badge;
- [ ] selected thumbnail state;
- [ ] `N / 6` count;
- [ ] multi-select Add Images;
- [ ] sequential processing;
- [ ] pending-memory guard;
- [ ] Set Primary;
- [ ] Remove;
- [ ] one normal Save;
- [ ] Cancel discards gallery changes;
- [ ] View mode thumbnail browsing;
- [ ] Copy starts with zero images;
- [ ] compact layout preserved;
- [ ] responsive/dark/light/keyboard usable;
- [ ] IIS base path safe.

## Regression

- [ ] old single-image item works;
- [ ] old ImagePath readers work;
- [ ] Stock Master create/edit/view/copy work;
- [ ] current validations unchanged;
- [ ] Class/Subclass unchanged;
- [ ] Warehouse/Location unchanged;
- [ ] Lot/Expiry unchanged;
- [ ] structural locks unchanged;
- [ ] delete protection unchanged;
- [ ] no posting/costing/ledger/month-end change;
- [ ] no DevExpress expression error;
- [ ] solution builds/tests.

---

# 78. Approval Decision

## **APPROVED FOR IMPLEMENTATION — 10/10**

Final recommended design:

```text
IvStockMaster
    ImagePath  ----------------------------┐
                                          │ canonical primary
                                          ▼
IvStockMasterImage
    Uid
    CompanyCode
    ICode
    ImagePath
    SortOrder
    CreatedDate
    CreatedBy
```

Practical operating rule:

```text
1 Primary + maximum 5 additional images
```

This is enough flexibility for machinery, spare parts, hardware, furniture, electrical products, packaging, automotive parts, and similar SME use cases without adding unnecessary complexity.

**Final status: APPROVED FOR IMPLEMENTATION.**

The coding agent must begin with Phase 0 against the actual working tree containing the completed single-image implementation, then follow this plan without introducing a parallel image subsystem.
