# Work Order Simplified Create / Save Draft / Release UX
## APPROVED implementation plan for Cursor / Grok 4.7 Code Agent

**Status:** APPROVED  
**Implementation readiness:** 10/10  
**Target repository:** `mokth/net10projectTemplate`  
**Target branch:** `production`  
**Repository baseline reviewed:** commit `ae35b23d7c8853d89d2a3fa5f44313f12f524b6e`  
**Primary UI:**  
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

**Primary service / domain files reviewed:**  
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
- `ErpWeb.Core/Production/WorkOrderSnapshotBuilder.cs`
- `ErpWeb.Core/Production/WorkOrderReadinessValidator.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderOptions.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrder.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderConfiguration.cs`
- `ErpWeb.Model/Entities/Production/ProductionReadinessErrorCodes.cs`

**Tests reviewed:**  
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`

**Related SQL reviewed:**  
- `scripts/alter-prworkorder-snapshot-format-v3.sql`

---

# 1. Objective

Simplify Work Order creation so a normal ERP user does **not** need to understand or manually execute internal snapshot lifecycle steps.

The current user-reported workflow is:

1. Enter required Work Order data.
2. Click **SAVE DRAFT**.
3. Click **REFRESH DEFINITION**.
4. Click **RELEASE**.

That workflow is confusing and should not be the normal path.

The approved target workflow is:

### Normal new Work Order

```text
Enter Work Order
      ↓
RELEASE
      ↓
Work Order created and released
```

or, when the user is not ready:

```text
Enter Work Order
      ↓
SAVE DRAFT
      ↓
Draft retained for later work
```

For an existing Draft with normal unsaved header changes:

```text
Edit quantity / date / reference / remark
      ↓
RELEASE
      ↓
Changes are applied and Work Order released atomically
```

`Refresh Definition` remains available only when it has real business meaning:

- upgrading an old/legacy snapshot;
- intentionally rebuilding a Draft from the latest ACTIVE revision of the same Product Definition.

It must **not** be a mandatory step for every newly created Work Order.

---

# 2. Repo-verified reason this change is correct

The existing backend already supports the intended simplified business model.

## 2.1 New Draft creation already builds the current snapshot

`ProductionWorkOrderService.DraftCommands.cs::CreateDraftAsync()` calls:

```csharp
BuildCurrentSnapshotAsync(
    scope,
    request,
    snapshotRevision: 1,
    explicitScheduleAnchor: null,
    cancellationToken);
```

`BuildCurrentSnapshotAsync()` invokes the Work Order snapshot builder and scheduler before persistence.

`WorkOrderSnapshotBuilder.FinalizeAsync()` stamps:

```csharp
workOrder.SnapshotHashVersion = ProductionSnapshotHashVersions.Current;
workOrder.SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current;
workOrder.IsLegacySnapshot = false;
workOrder.LegacySnapshotReason = null;
```

Therefore a newly created Work Order should already contain:

- selected Product Definition;
- source Product Definition revision;
- BOM snapshot;
- route;
- operations;
- materials;
- machines/labour;
- calculated quantities;
- schedule;
- snapshot hash;
- current snapshot format.

A new Work Order does **not** need an immediate Refresh Definition just to become releaseable.

## 2.2 Existing tests already prove this intended behavior

`ProductionWorkOrderServiceTests` contains:

- `Preview_and_create_use_the_same_current_snapshot_pipeline`
- `Create_header_update_and_release_use_the_version2_snapshot`
- `Release_stays_blocked_when_the_feature_is_disabled`

The current create/release test explicitly asserts:

```csharp
Assert.Equal(
    ProductionSnapshotFormatVersions.Current,
    detail.SnapshotFormatVersion);

Assert.False(detail.IsLegacySnapshot);
```

and then successfully calls `ReleaseCurrentAsync(...)`.

This is strong repo evidence that:

> New Draft → current snapshot → Release

is already the intended architecture.

## 2.3 Refresh has a different purpose

`RefreshDraftFromDefinitionAsync(...)`:

- rebuilds an already saved Draft;
- re-resolves its Product Definition;
- replaces the snapshot;
- resets structural machine/material overrides to definition defaults;
- requires a reason;
- has concurrency protection against Product Definition changes after preview.

This is a controlled structural operation, not a normal Save prerequisite.

## 2.4 Legacy snapshot refresh remains valid

`WorkOrderReadinessValidator` blocks old snapshots with:

```text
WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED
```

and `scripts/alter-prworkorder-snapshot-format-v3.sql` confirms the current format is v3.

Do not remove this protection.

---

# 3. Non-negotiable business rules

The Code Agent must preserve all of these rules.

1. **Do not silently refresh an existing Draft from a newer Product Definition during normal Save or Release.**
2. A Work Order snapshot remains frozen after it is built unless the user explicitly:
   - uses Change Definition; or
   - uses Update/Refresh from Product Definition.
3. New Work Orders must build the current snapshot from the selected ACTIVE Product Definition.
4. Release must continue to execute:
   - readiness validation;
   - snapshot integrity validation;
   - schedule freshness validation;
   - permission validation;
   - concurrency validation where applicable.
5. Release still does **not** post inventory.
6. Existing `ReleaseEnabled` fail-closed configuration remains enforced.
7. Reopen/rollback rules are outside this change and must not be weakened.
8. No Work Order master-data history may be rewritten by later Product Definition edits.
9. After a Work Order is first persisted, `ProductCode`, `SourceDefinitionCode`, and `SourceType` are immutable through normal header edit/release. Definition changes continue only through the explicit Change Definition workflow.
10. A legacy Work Order (`SnapshotFormatVersion < Current`) must be blocked from Release **server-side as well as in the UI** and must return `WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED`.
11. Do not remove snapshot revision/hash/provenance fields.
12. Do not bypass the current scheduler or readiness validator.
13. Before mutating an existing current-format Draft, verify its persisted snapshot hash against a fresh recomputation. Never overwrite/rehash a corrupted persisted snapshot and thereby make it look valid.

---

# 4. Target user experience

## 4.1 New mode

Primary command bar:

```text
BACK      HOW TO USE

                      SAVE DRAFT     MORE     RELEASE
```

`RELEASE` must be available in New mode when:

- user has Add permission;
- user has Release permission;
- the screen is not submitting;
- required entry fields have sufficient values to attempt validation.

The user must **not** have to Save Draft first.

If validation fails, remain on the New page and show the current service validation errors.

## 4.2 Existing current-format Draft

Primary command bar:

```text
SAVE DRAFT     MORE     RELEASE
```

After the first successful save, the normal edit surface must lock identity fields:

```text
Product             LOCKED
Product Definition  LOCKED
Source Type         LOCKED

Planned Qty          editable
Schedule             editable
Source Reference     editable
Remark               editable
```

This matches the current service contract: `UpdateDraftHeaderAsync(...)` does not change Product, Definition, or Source Type. Do not allow the UI to display a different Product or Source Type that the service will ignore.

If there are unsaved normal header changes, `RELEASE` should remain available.

On Release:

1. apply the current input values;
2. recalculate quantity/schedule where required;
3. run release readiness;
4. release in the same service transaction.

Do not force:

```text
SAVE DRAFT → RELEASE
```

for ordinary quantity/date/reference/remark edits.

## 4.3 Legacy Draft

If:

```csharp
DetailModel.SnapshotFormatVersion
    < ProductionSnapshotFormatVersions.Current
```

retain the exceptional upgrade path.

Show an explicit primary action such as:

```text
UPDATE WORK ORDER DEFINITION
```

or retain current `REFRESH DEFINITION` wording if minimizing UI scope.

Release remains blocked until legacy snapshot upgrade succeeds.

## 4.4 Refresh command placement

For a normal current-format Draft:

Move/keep the command only under `MORE`.

Preferred user-facing text:

```text
UPDATE FROM PRODUCT DEFINITION
```

rather than technical wording:

```text
REFRESH DEFINITION
```

The existing popup behavior may remain, including:

- preview of added/removed/changed structural rows;
- mandatory reason;
- confirmation;
- replacement of route/material/machine snapshot.

For the initial implementation, renaming is optional if it creates excessive unrelated UI churn. The important rule is that this command is not part of normal New → Release processing.

---

# 5. Service design

Do **not** solve this by chaining UI calls:

```csharp
await CreateDraftAsync(...);
await ReleaseCurrentAsync(...);
```

or:

```csharp
await UpdateDraftHeaderAsync(...);
await ReleaseCurrentAsync(...);
```

from the Razor component.

That would create two independent business transactions and allow partial completion.

Example bad result:

```text
CreateDraft succeeds
Release fails
user expected "Release"
but receives an unexpected saved Draft
```

The combined operation must be implemented in the service layer.

---

# 6. Add explicit service commands

Modify:

`ErpWeb.Core/Production/IProductionWorkOrderService.cs`

Add two explicit application commands.

## 6.1 New Work Order: Create and Release

Recommended contract:

```csharp
Task<IvMasterOperationResult<ProductionWorkOrderDetail>>
    CreateAndReleaseAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default);
```

This command is specifically for New mode.

## 6.2 Existing Draft: Update header and Release

Add a dedicated request model, e.g.:

```csharp
public sealed class ProductionWorkOrderUpdateAndReleaseRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;

    public decimal PlannedQty { get; set; }

    public string SchedulingDirection { get; set; }
        = ProductionSchedulingDirections.Forward;

    public DateTime? ScheduleAnchorDateTime { get; set; }

    public string? SourceReference { get; set; }

    public string? Remark { get; set; }

    public byte[] RowVersion { get; set; } = [];

    // Fingerprint of the saved Draft the user opened.
    public int SnapshotRevision { get; set; }

    public string SnapshotHash { get; set; } = string.Empty;

    public long? SourceProductDefinitionRevisionId { get; set; }
}
```

and service method:

```csharp
Task<IvMasterOperationResult<ProductionWorkOrderDetail>>
    UpdateAndReleaseAsync(
        ProductionWorkOrderUpdateAndReleaseRequest request,
        CancellationToken cancellationToken = default);
```

Do not put ProductCode or DefinitionCode in this command.

Existing saved Draft identity remains immutable through this command.

Product Definition changes must continue through the existing explicit Change Definition workflow.

## 6.3 Exact permission contract

The current repository defines:

```csharp
public static class ProductionPermissionCodes
{
    public const string Release = PermissionCodes.Approve;
}
```

The implementation must preserve this exact authority model:

| User action | Required permission |
|---|---|
| New Work Order → Save Draft | `ADD` |
| New Work Order → Release | `ADD` + `APPROVE` |
| Existing unchanged current Draft → Release | `APPROVE` |
| Existing changed current Draft → Release | `EDIT` + `APPROVE` |
| Existing Draft → Save changes | `EDIT` |
| Legacy Draft → Release | forbidden until explicit Refresh/Upgrade |

Do not accidentally require `EDIT` merely to release an unchanged Draft.
Do not invent a new Release permission; continue using `ProductionPermissionCodes.Release` (`APPROVE`).


---

# 7. Refactor internal release implementation safely

Primary file:

`ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`

The current `FinishCurrentReleaseAsync(...)` performs:

- ReleaseEnabled check;
- Draft status check;
- scheduling lock;
- client row-version validation;
- snapshot fingerprint validation;
- WorkOrderReadinessValidator;
- schedule-source hash freshness checks;
- status transition to Released;
- audit event;
- SaveChanges;
- commit.

This logic is valuable and must not be duplicated.

Refactor it into two conceptual layers.

## 7.1 Caller-specific concurrency validation

Existing `ReleaseCurrentAsync(...)` must continue validating:

- supplied `RowVersion`;
- `SnapshotRevision`;
- `SnapshotHash`;
- `SourceProductDefinitionRevisionId`.

This protects a user releasing an already-open Draft.

## 7.2 Shared Release core

Extract/reuse an internal helper that receives a tracked Work Order and performs the invariant Release rules:

- `ReleaseEnabled`;
- status is Draft;
- scheduling lock is held/acquired correctly;
- recompute snapshot hash;
- `_readiness.Validate(...)`;
- schedule freshness via `_scheduler.CurrentScheduleHashesAsync(...)`;
- set Released status/date/user;
- append Released audit event.

Example conceptual split only:

```csharp
ValidateReleaseRequestFingerprint(...);

await ValidateReleaseReadinessAsync(...);

ApplyReleasedState(...);
```

Do not weaken the existing fingerprint checks for normal existing-Draft Release.

For a newly created Work Order inside `CreateAndReleaseAsync`, there is no stale client fingerprint to validate because the aggregate was just built by the same server command.

## 7.3 Close the legacy server-side Release compatibility gap

The current repository still exposes:

```csharp
ReleaseAsync(string workOrderNo, byte[] rowVersion)
```

and its legacy branch can release an older snapshot without going through the current readiness validator.

This implementation must change that behavior.

For:

```text
SnapshotFormatVersion < ProductionSnapshotFormatVersions.Current
```

`ReleaseAsync(...)` must fail with:

```text
IvMasterErrorCode.Validation
WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED
```

and leave the Work Order in Draft.

The legacy UI is already blocked, but the server must enforce the same invariant. Do not rely on button visibility for business integrity.

## 7.4 Shared helper transaction ownership

Any extracted internal helper used by more than one public command must **not**:

- create/dispose a transaction;
- call `CommitAsync`;
- call `RollbackAsync`;
- decide whether a public command should return early after a no-op;
- own navigation/UI concerns.

Recommended responsibilities:

```text
ApplyDraftHeaderChangesAsync(...)
    → validate persisted snapshot
    → mutate tracked aggregate
    → recalculate qty/schedule when required
    → update hash/revision/audit when changed
    → return { Changed = true/false }

ValidateAndApplyCurrentReleaseAsync(...)
    → validate ReleaseEnabled/status/readiness/schedule freshness
    → set Released fields
    → append Released audit
```

The public command owns:

```text
Begin transaction
SaveChanges
Commit / Rollback
```

This is critical because current `UpdateDraftHeaderAsync(...)` rolls back and returns early when no effective change exists. That rollback behavior must **not** be copied into a shared helper used by `UpdateAndReleaseAsync(...)`.


---

# 8. CreateAndReleaseAsync transaction

Implement in:

`ProductionWorkOrderService.DraftCommands.cs`

## Required sequence

```text
Authorize ADD
Authorize RELEASE
      ↓
BuildCurrentSnapshotAsync
      ↓
Begin DB transaction
      ↓
Allocate Work Order number
      ↓
Stamp Created metadata/audit
      ↓
Add Work Order aggregate
      ↓
SaveChanges
      ↓
Run shared Release readiness/freshness rules
      ↓
Set Released metadata/audit
      ↓
SaveChanges
      ↓
Commit
```

### Important

The initial `SaveChanges` occurs **inside the same database transaction**.

This allows:

- SQL Server identity keys;
- rowversion generation;
- child keys/relations;

to be materialized before final Release, while preserving atomic behavior.

If any Release validation fails after the first `SaveChanges`:

```text
ROLLBACK entire transaction
```

No Draft must remain in the database.

This is a hard acceptance criterion.

The allocated Work Order running number may legitimately be consumed by a failed transaction only if the existing `RunningNumberService`/database transaction semantics cause that behavior. Do not introduce a second independent numbering transaction. The critical invariant is **no persisted Work Order aggregate**, not gapless numbering.


## Permissions

The combined command requires both:

- existing Add permission;
- existing Production Work Order Release permission.

Use the same permission constants already used by:

- `CreateDraftAsync`
- `ReleaseCurrentAsync`

Do not invent a new permission.

## Audit

A successful one-click Release must still create both lifecycle events:

1. `Created`
2. `Released`

Do not replace these with a single combined event.

This preserves audit semantics.

---

# 9. UpdateAndReleaseAsync transaction

This command handles a saved current-format Draft whose visible header inputs may have changed.

The implementation must reuse the logic currently inside:

`UpdateDraftHeaderAsync(...)`

including:

- current-snapshot requirement;
- direction validation;
- schedule-anchor calculation;
- quantity recalculation;
- schedule recalculation;
- snapshot hash recomputation;
- snapshot revision increment when the snapshot changes;
- DraftUpdated audit behavior where appropriate.

Then release in the same transaction.

## Required sequence

```text
Authorize EDIT
Authorize RELEASE
      ↓
Begin transaction
      ↓
Load Draft aggregate using RowVersion
      ↓
Require current snapshot format
      ↓
Validate caller fingerprint:
    SnapshotRevision
    SnapshotHash
    SourceProductDefinitionRevisionId
      ↓
Recompute persisted snapshot hash BEFORE mutation
      ↓
If recomputed hash != stored SnapshotHash:
    fail WO_SNAPSHOT_HASH_INVALID
    rollback
      ↓
Apply allowed header changes
      ↓
If quantity changed:
    Calculate quantities
      ↓
If quantity/schedule changed:
    schedule
      ↓
Recompute new hash/revision if changed
      ↓
Run Release readiness/freshness
      ↓
Set Released state
      ↓
SaveChanges
      ↓
Commit
```

### No definition refresh

Do not call:

- `PreviewRefreshFromDefinitionAsync`
- `RefreshDraftFromDefinitionAsync`
- `BuildCurrentSnapshotAsync`

for an existing normal Draft release solely because Product Definition master data has changed.

The Draft must release from its saved snapshot unless the user explicitly refreshes it first.

## 9.1 Mandatory pre-mutation persisted-snapshot integrity check

Before changing Qty, schedule, reference, or remark on an existing Draft, recompute the hash from the tracked persisted aggregate and compare it with `entity.SnapshotHash`.

Conceptually:

```csharp
var persistedHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);

if (!string.Equals(
        persistedHash,
        entity.SnapshotHash,
        StringComparison.Ordinal))
{
    throw new WorkOrderCommandException(
        IvMasterErrorCode.Validation,
        ProductionReadinessErrorCodes.SnapshotHashInvalid
        + ": The saved Work Order snapshot failed its integrity check.");
}
```

This check must happen **before** assigning new header values and before writing a new hash.

Apply the same integrity rule to normal `UpdateDraftHeaderAsync(...)` while extracting shared header-update logic. Otherwise a corrupted persisted graph could be rehashed during an ordinary header update and made to look valid.

Do not compare the saved Draft against the latest Product Definition master here. This is only a self-integrity check of the frozen Work Order snapshot.


---

# 10. Avoid duplicated header-update logic

Do not copy/paste the body of `UpdateDraftHeaderAsync(...)`.

Extract an internal tracked-entity helper, for example conceptually:

```csharp
private async Task ApplyDraftHeaderChangesAsync(
    AppDbContext db,
    ProductionWorkOrder entity,
    ...,
    CancellationToken cancellationToken)
```

Both:

- `UpdateDraftHeaderAsync`
- `UpdateAndReleaseAsync`

must use the same calculation/scheduling rules.

The helper must not:

- call `SaveChangesAsync`;
- call `CommitAsync`;
- call `RollbackAsync`;
- create/dispose the transaction.

Return an explicit result such as:

```csharp
private sealed record DraftHeaderMutationResult(bool Changed);
```

For `UpdateDraftHeaderAsync`, `Changed == false` may cause the public command to return without persisting.

For `UpdateAndReleaseAsync`, `Changed == false` must **not** end the command; it must continue into Release.

The public commands own their transactions.

This prevents Save Draft and Release from producing different quantity/schedule results and avoids an extracted helper accidentally rolling back the outer Update+Release transaction.

---

# 11. Razor code-behind changes

Modify:

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

## 11.1 Replace current Release gating

Current:

```csharp
protected bool CanReleaseAction =>
    DetailModel is { Status: ProductionWorkOrderStatuses.Draft }
    && CanRelease
    && IsCurrentSnapshot
    && !HasUnsavedInputChanges;
```

This is one source of the frustrating UX.

Replace with logic that handles three states.

Conceptually:

```csharp
protected bool CanReleaseAction =>
    !IsLoading
    && !IsSubmitting
    && CanRelease
    && (
        (IsNewMode && CanAdd)
        ||
        (DetailModel is { Status: ProductionWorkOrderStatuses.Draft }
            && IsCurrentSnapshot
            && (!HasUnsavedInputChanges || CanEdit))
    );
```

Do not enable Release for legacy saved Drafts.

This distinction is mandatory in the current repository:

- unchanged current Draft requires Release (`APPROVE`) permission only;
- changed current Draft additionally requires `EDIT`;
- New → Release requires `ADD` + Release (`APPROVE`).

Do not make `EDIT` a blanket requirement for every existing Draft release.



## 11.2 Lock persisted Work Order identity in the UI

The current page enables the Product picker with `CanEditInputs`, which means a saved Draft can visually change Product even though `UpdateDraftHeaderAsync(...)` does not persist Product.

Fix this explicitly.

Recommended properties:

```csharp
protected bool CanEditProduct =>
    IsNewMode && CanEditInputs;

protected bool CanEditSourceType =>
    IsNewMode && CanEditInputs;
```

Use them in `PrWorkOrderEntry.razor`:

```razor
<IvStockMasterPicker
    ...
    Enabled="@CanEditProduct" />

<DxComboBox
    Data="@SourceOptions"
    ...
    Enabled="@CanEditSourceType" />
```

After first persistence:

```text
Product             read-only
Product Definition  read-only except explicit Change Definition workflow
Source Type         read-only
```

This prevents the screen from showing an identity value that the service ignores.

## 11.3 ReleaseAsync dispatch

Change `ReleaseAsync()` to dispatch by mode/state.

### New mode

Call:

```csharp
PrepareRequestIdentity();
await WorkOrders.CreateAndReleaseAsync(Request);
```

No Save Draft call first.

The current method begins with:

```csharp
if (DetailModel is null)
{
    return;
}
```

That guard must be restructured because `DetailModel == null` is valid in New mode.

Required control flow:

```csharp
if (IsNewMode)
{
    // CreateAndReleaseAsync(Request)
}
else if (DetailModel is null)
{
    return;
}
else if (HasUnsavedInputChanges)
{
    // UpdateAndReleaseAsync(...)
}
else
{
    // ReleaseCurrentAsync(...)
}
```

Do not leave the old early return in front of the New-mode branch.


### Existing Draft with unsaved header changes

Call:

```csharp
await WorkOrders.UpdateAndReleaseAsync(...)
```

with the current screen values plus the saved Draft fingerprint:

```text
DetailModel.RowVersion
DetailModel.SnapshotRevision
DetailModel.SnapshotHash
DetailModel.SourceProductDefinitionRevisionId
```

The service validates that fingerprint before mutating the aggregate.

### Existing Draft without unsaved input changes

Keep the existing:

```csharp
WorkOrders.ReleaseCurrentAsync(...)
```

path.

### Legacy Draft

Do not release.

Surface the existing legacy-refresh instruction.

---

# 12. Release confirmation popup

Modify:

`PrWorkOrderEntry.razor`

Current popup assumes a Work Order number already exists:

```text
Release @CurrentWorkOrderNo for execution?
```

This is incorrect for New mode.

Make the text state-aware.

## New mode

Suggested:

```text
Create and release this Work Order for production execution?
```

Secondary text:

```text
The Work Order will be created from the selected Product Definition,
its route/material/schedule snapshot will be validated, and it will
be released for execution. No inventory transaction will be posted.
```

Buttons:

```text
Continue Editing     Release
```

## Existing Draft

Retain current meaning:

```text
Release WOxxxx for execution?
```

If there are unsaved changes, add:

```text
Your current quantity, schedule and header changes will be saved as
part of Release.
```

Do not mention snapshot hashes or internal format versions in the normal popup.

---

# 13. Command bar changes

Modify:

`PrWorkOrderEntry.razor`

## New mode

Show both:

- `SAVE DRAFT`
- `RELEASE`

The user chooses the business intention.

Do not require Save Draft to make Release visible.

## Existing Draft

Continue showing:

- Save Draft
- Release

Release must remain usable with normal pending header changes.

## Refresh Definition

For `NeedsDefinitionUpgrade == true`, it may remain prominent because the user genuinely must upgrade the old Draft.

For a current snapshot, keep Refresh under `MORE`.

Do not make Refresh appear as the next required step after Save Draft.

---

# 14. Help text changes

The current Help text explicitly says:

```text
Unsaved input changes hide Release...
```

That must be removed because it will no longer be true.

Update Help to explain:

### Save Draft

```text
Saves the Work Order without releasing it for production.
Use this when the Work Order is not ready.
```

### Release

```text
Creates/releases a new Work Order, or saves current Draft header
changes and releases the Draft in one operation.
```

### Calculate Preview

Keep optional:

```text
Calculates the route, materials and schedule for inspection without
saving.
```

It must not become a required pre-release step.

### Refresh / Update from Product Definition

Explain clearly:

```text
Use only when you intentionally want an existing Draft to adopt the
current Product Definition. This can change routing, materials,
machines and schedule.
```

## 14.1 Update all inline status/warning text, not only the Help popup

Current code-behind contains wording equivalent to:

```text
Inputs differ from the saved snapshot.
Use Calculate Preview to inspect the changes,
or Save Draft to apply them.
```

That becomes misleading after direct Release is supported.

Change it to business-intent wording such as:

```text
Inputs differ from the saved Work Order.
Save Draft to keep the changes as Draft,
or Release to apply them and release the Work Order.
```

The section note currently says:

```text
Save applies required recalculation.
```

Update it to:

```text
Save Draft or Release applies required quantity and schedule recalculation automatically.
Calculate Preview is optional.
```

Do not leave any normal-path message that trains the user to Save Draft before Release.


---

# 15. Definition selection behavior

Keep the current behavior in:

`LoadDefinitionOptionsAsync(...)`

The repo already:

- loads ACTIVE Product Definitions;
- prefers the authored default;
- uses a single available definition automatically;
- falls back to STANDARD where applicable.

Do not introduce an extra definition-selection step when one/default definition is already resolved.

When multiple definitions exist and no deterministic default applies, the user must still choose the intended definition.

Do not silently select an ambiguous definition.

---

# 16. Do not automatically track later Product Definition changes

Example:

```text
09:00  WO draft built from STANDARD V5
10:00  engineer changes Product Definition to V6
11:00  user releases existing WO draft
```

Approved behavior:

```text
Release the saved V5 Work Order snapshot
```

unless the user explicitly selects:

```text
MORE → UPDATE FROM PRODUCT DEFINITION
```

This is essential for production traceability.

Do not compare the saved `DefinitionSourceHash` against the latest Product Definition as a mandatory normal Release gate.

The saved Work Order is intentionally a historical snapshot.

---

# 17. Legacy snapshot handling

Do not alter the current integrity rule:

```text
SnapshotFormatVersion < Current
→ no structural edit/release
→ explicit Refresh/Upgrade required
```

Continue using:

`ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired`

Do not automatically upgrade historical Drafts during Release without user acknowledgement because the structural definition may change.

---

# 18. Error handling

Combined Release commands must return existing `IvMasterOperationResult<T>` semantics.

Preserve:

- `ValidationErrors`
- `AccessDenied`
- `Concurrency`
- `NotFound`
- `DuplicateKey`
- readiness codes
- scheduling failure codes

The Razor page should continue routing failures through existing:

```csharp
ApplyFailure(result);
```

If New-mode CreateAndRelease fails:

- remain in New mode;
- do not navigate;
- do not leave a saved Draft;
- keep the user's current entered values.

If existing UpdateAndRelease fails:

- remain on Edit;
- keep entered values visible where practical;
- do not partially save the header if transaction rolled back.

---

# 19. Concurrency requirements

Preserve existing strong concurrency behavior.

Existing current Draft release must still protect:

- `RowVersion`
- `SnapshotRevision`
- `SnapshotHash`
- `SourceProductDefinitionRevisionId`

Existing SQL Server tests already cover:

- `Two_releases_with_the_same_tokens_leave_exactly_one_released_order`
- `Draft_header_update_racing_release_leaves_a_consistent_aggregate`

Do not bypass these checks in `ReleaseCurrentAsync`.

`UpdateAndReleaseAsync` must use the supplied Draft RowVersion as its initial optimistic-concurrency boundary.

New `CreateAndReleaseAsync` does not need a client RowVersion because no persisted Work Order existed when the command started.

---

# 20. Scheduling lock requirements

Keep the existing `WorkOrderSchedulingLock`.

The combined commands must not create a second scheduling path.

Use the same lock semantics currently used for:

- header quantity/schedule changes;
- Release schedule-source verification.

If lock ownership/refactoring makes nested lock acquisition unsafe, refactor to acquire once at the outer command and allow internal helpers to assume it is held.

Do not remove the lock.

---

# 21. SQL Server deployment preflight

No new schema migration is required for this feature, but the existing database must already allow the current snapshot format.

Before acceptance/deployment, verify:

```sql
SELECT name, definition
FROM sys.check_constraints
WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrder')
  AND name = N'CK_PrWorkOrder_SnapshotFormat';
```

The constraint must allow snapshot format `3`.

If the environment still allows only formats 1/2, run the repository's existing idempotent script:

```text
scripts/alter-prworkorder-snapshot-format-v3.sql
```

Do not invent a new migration for this UX change.

---

# 22. SQL Server production-behavior regression

The user reports behavior that contradicts the current unit-test intent.

Therefore add a SQL Server regression test to:

`ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`

or the closest existing SQL Server Work Order fixture.

Test name recommendation:

```csharp
New_current_snapshot_can_create_and_release_without_definition_refresh()
```

Required assertions:

```text
CreateAndReleaseAsync(request)
    succeeds

Work Order exists
Status == Released
SnapshotFormatVersion == Current
IsLegacySnapshot == false
SnapshotHashVersion == Current
SourceProductDefinitionRevisionId != null
DefinitionSourceHash is populated
RouteSteps > 0
Operations > 0
expected Materials exist
ReleasedDate != null
Created audit exists
Released audit exists
```

Also assert no refresh audit is required/created.

Add/retain SQL Server concurrency coverage for:

```text
Two_releases_with_the_same_tokens_leave_exactly_one_released_order
Draft_header_update_racing_release_leaves_a_consistent_aggregate
```

and add an UpdateAndRelease race case if the refactor changes the locking/concurrency surface:

```text
Update_and_release_with_stale_tokens_never_partially_updates_or_releases
```

For failed `CreateAndReleaseAsync`, verify on SQL Server that the Work Order aggregate does not remain persisted after transaction rollback.


This test is important because the production complaint may expose SQL Server/schema behavior that SQLite unit tests do not catch.

---

# 23. Required unit tests

Extend:

`ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`

At minimum add the following.

## New create/release

```text
Create_and_release_builds_current_snapshot_and_releases
```

Verify:

- current format;
- not legacy;
- route/material/operation snapshot exists;
- released status;
- exactly one Created audit;
- exactly one Released audit;
- no Refreshed audit.

## Atomic failure

```text
Create_and_release_failure_does_not_leave_draft
```

Cause a release-readiness failure.

Verify:

```text
result failed
no Work Order persisted
```

## Release disabled

```text
Create_and_release_rolls_back_when_release_feature_disabled
```

Verify:

- `WO_RELEASE_DISABLED`;
- no Work Order persisted.

## Missing Add permission

```text
Create_and_release_requires_add_permission
```

## Missing Release permission

```text
Create_and_release_requires_release_permission
```

Verify no Work Order persisted in both cases.

## Existing Draft with qty change

```text
Update_and_release_applies_qty_recalculation_then_releases
```

Verify:

- final PlannedQty;
- material RequiredQty recalculated;
- schedule recalculated when applicable;
- snapshot hash/revision consistent;
- final status Released.

## Existing Draft with remark only

```text
Update_and_release_applies_header_change_then_releases
```

## Concurrency

```text
Update_and_release_rejects_stale_rowversion
```

No partial header update must persist.

## Definition freeze

```text
Release_does_not_auto_refresh_changed_product_definition
```

Sequence:

1. Create Draft from definition revision A.
2. Modify Product Definition master.
3. Release saved Draft.
4. Verify WO remains on its saved source/snapshot A.

This test is mandatory.

## Persisted snapshot tamper before header update/release

```text
Update_header_rejects_corrupted_saved_snapshot
Update_and_release_rejects_corrupted_saved_snapshot
```

Sequence:

1. Create current-format Draft.
2. Tamper a persisted child/material/operation so recomputed hash no longer matches stored `SnapshotHash`.
3. Call header update / UpdateAndRelease.
4. Verify validation fails with `WO_SNAPSHOT_HASH_INVALID`.
5. Verify no new snapshot hash/revision is written and the Work Order is not released.

These tests are mandatory.

## Legacy server-side Release block

```text
Legacy_snapshot_release_requires_explicit_refresh
```

Verify:

- legacy Draft remains Draft;
- `ReleaseAsync(...)` returns Validation;
- message/code includes `WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED`;
- no Released audit is written.

## Saved identity immutability

Service-level request models already omit Product/Definition/SourceType for normal header update. Add at least one UI/manual acceptance check proving that after first save:

- Product cannot be changed by the normal entry form;
- Source Type cannot be changed by the normal entry form;
- Product Definition can change only through the existing explicit Change Definition command.


---

# 24. Razor/component behavior tests

If this repo has no practical bUnit coverage for this page, do not introduce a new UI-test framework solely for this change.

Instead ensure service tests cover business atomicity and manually verify the page.

If there is an established Razor component testing pattern already in the repo, add tests using that existing pattern only.

---

# 25. Manual acceptance scenarios

The implementation is not complete until all scenarios below are verified.

## Scenario A — new WO, normal release

1. Open New Work Order.
2. Select product.
3. Default/single Product Definition resolves.
4. Enter qty/date.
5. Click RELEASE.
6. Confirm.

Expected:

```text
WO created
WO Released
no Save Draft required
no Refresh Definition required
no inventory posted
```

## Scenario B — new WO, save only

1. Enter new WO.
2. Click SAVE DRAFT.

Expected:

```text
Draft created
current snapshot stored
Release available immediately
Refresh not required
```

## Scenario C — saved Draft, no changes

Open Draft and Release.

Expected immediate Release using current fingerprint checks.

## Scenario D — saved Draft, quantity changed

1. Open Draft.
2. Change qty.
3. Click Release directly.

Expected:

```text
quantity applied
material quantities recalculated
schedule recalculated if required
released
```

No separate Save Draft.

## Scenario E — changed Product Definition master

1. Save Draft from definition V5.
2. Change master to V6.
3. Open V5 Draft.
4. Release without Refresh.

Expected:

```text
WO releases its saved V5 snapshot
```

## Scenario F — explicit Update from Product Definition

1. Save Draft.
2. Change master definition.
3. Use MORE → Refresh/Update from Product Definition.
4. Review structural diff.
5. Enter reason.
6. Confirm.
7. Release.

Expected V6/current revision is deliberately adopted.

## Scenario G — legacy Draft

Open old format 1/2 Draft.

Expected:

- Release blocked.
- Upgrade/Refresh action clearly shown.
- after successful Refresh, normal Release available.

## Scenario H — create-and-release readiness failure

Use a definition with an invalid route/readiness issue.

Expected:

- error displayed;
- no Work Order row left behind.

## Scenario I — concurrent existing Draft change

User A opens Draft.
User B changes Draft.
User A clicks Release.

Expected concurrency failure and reload path.

## Scenario J — saved identity fields are frozen

1. Create and Save Draft for FG001.
2. Reopen Edit.
3. Verify Product and Source Type are read-only.
4. Verify Product Definition is read-only on the normal form.
5. Use More → Change Definition to confirm the controlled definition-change workflow still works.

## Scenario K — persisted snapshot corruption

1. Create a current Draft in test data.
2. Tamper a child snapshot row directly in the test database.
3. Attempt Save Draft header update or Update+Release.

Expected:

```text
operation rejected
WO_SNAPSHOT_HASH_INVALID
no new hash/revision written
status remains Draft
```

## Scenario L — legacy API/service release attempt

Call the legacy `ReleaseAsync(workOrderNo, rowVersion)` service path for an old snapshot.

Expected:

```text
blocked server-side
WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED
status remains Draft
```


---

# 26. Files expected to change

## Required

1. `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
   - add CreateAndRelease contract;
   - add UpdateAndRelease request/contract.

2. `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
   - implement combined commands;
   - refactor shared header mutation logic;
   - refactor shared release core;
   - preserve atomic transaction.

3. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`
   - New-mode Release availability;
   - existing unsaved Draft Release;
   - dispatch to combined service commands.

4. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
   - command visibility;
   - Release popup text;
   - Help text;
   - optional user-facing Refresh wording.

5. `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
   - combined command tests.

6. `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`
   - SQL Server create/release regression;
   - concurrency coverage if helper refactor changes release internals.

## Also required

7. `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
   - close the legacy `ReleaseAsync(...)` compatibility path so legacy snapshots cannot be released server-side;
   - preserve current permission and scope behavior.

## Change only if required by compilation/refactor

- `ProductionWorkOrderRules.cs`

## Should not require schema changes

No new table or column is required for this feature.

Do not create a migration merely for this UX simplification.

The existing Work Order schema already stores everything required.

---

# 27. Existing behavior that must remain unchanged

Do not regress:

- Product Definition explicit selection;
- Change Definition preview/confirmation;
- Refresh structural diff;
- material alternate substitution;
- machine selection;
- schedule recalculation;
- Work Order cancellation;
- Reopen for Edit;
- Issue Materials navigation;
- route/operation/material display;
- permissions;
- audit events;
- snapshot revision/hash;
- current/legacy snapshot distinction;
- ReleaseEnabled configuration;
- readiness rules;
- calendar freshness checks;
- SQL Server rowversion concurrency.
- saved Work Order Product/Definition/Source Type identity;
- server-side legacy snapshot Release prohibition.

---

# 28. Implementation order for Code Agent

Implement in this order.

### Phase 1 — service refactor

1. Add pre-mutation persisted-snapshot integrity validation to the tracked Draft header-update path.
2. Extract reusable tracked Draft header-update logic from `UpdateDraftHeaderAsync`, with **no transaction/save/commit/rollback ownership** in the helper.
3. Extract/restructure reusable current-snapshot Release core from `FinishCurrentReleaseAsync`, also without transaction commit/rollback ownership.
4. Close the legacy `ReleaseAsync(...)` path so old snapshots fail with `WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED`.
5. Keep all existing public current-format behavior passing tests before adding new UI behavior.

### Phase 2 — combined commands

6. Add `CreateAndReleaseAsync`.
7. Add fingerprint-complete `UpdateAndReleaseAsync`.
8. Add service unit tests for atomicity, tamper protection, legacy blocking, and permission combinations.
9. Verify all existing Work Order service tests still pass.

### Phase 3 — SQL Server regression

10. Verify the SQL Server snapshot-format-v3 CHECK constraint or run the existing repo script.
11. Add current-snapshot direct create/release SQL Server test.
12. Add/update SQL Server atomic rollback and concurrency coverage where required.
13. Run existing SQL Server concurrency tests when configured.

### Phase 4 — UI

14. Lock Product and Source Type after first persistence; keep Definition changes explicit.
15. Modify Release action availability using the exact ADD/EDIT/APPROVE matrix.
16. Restructure `ReleaseAsync()` so New mode is handled before the old `DetailModel is null` guard.
17. Dispatch New / changed Draft / unchanged Draft to the correct service command.
18. Update confirmation popup.
19. Update Help text **and inline snapshot/status warnings**.
20. Keep Refresh exceptional.

### Phase 5 — full regression

21. Run Planning transaction tests.
22. Run Production transaction tests affected by WO Release.
23. Run SQL Server Work Order tests when scratch DB is configured.
24. Build full solution.

Do not start by hiding/removing buttons before service atomicity is complete.

---

# 29. Build/test commands

Use the repo's existing solution/project structure.

At minimum:

```text
dotnet build
```

Run focused tests for:

```text
ErpWeb.Tests / Planning / Transaction
```

especially:

```text
ProductionWorkOrderServiceTests
ProductionWorkOrderSqlServerConcurrencyTests
WorkOrderReadinessValidatorTests
WorkOrderSnapshotHasherTests
ProductionWorkOrderCalcTests
```

If the repository has its own documented filtered-test command, use that established command.

Do not ignore failing existing Work Order tests.

---

# 30. Definition of done

This plan is complete only when all of the following are true.

- [ ] New Work Order can Release directly without Save Draft.
- [ ] New direct Release uses the selected Product Definition and current snapshot builder.
- [ ] New direct Release is one atomic application transaction.
- [ ] Failed direct Release leaves no Draft behind.
- [ ] Save Draft remains available as an intentional business choice.
- [ ] A saved current Draft can Release without Refresh Definition.
- [ ] A saved current Draft with normal unsaved header changes can Release directly.
- [ ] Update + Release is atomic.
- [ ] Later Product Definition edits do not silently rewrite saved Work Order snapshots.
- [ ] Refresh remains explicit for structural definition adoption.
- [ ] Legacy snapshots still require explicit upgrade.
- [ ] ReleaseEnabled still blocks Release.
- [ ] readiness validation is unchanged.
- [ ] schedule freshness validation is unchanged.
- [ ] rowversion/snapshot concurrency protection remains for existing Drafts.
- [ ] Created and Released audit events remain correct.
- [ ] no inventory transaction is posted by Work Order Release.
- [ ] existing Change Definition / machine / material workflows still work.
- [ ] SQLite/unit tests pass.
- [ ] SQL Server Work Order regression passes when SQL test DB is available.
- [ ] full project builds with no new warnings/errors attributable to this change.
- [ ] Product and Source Type are read-only after first Work Order persistence.
- [ ] normal saved-Draft Product Definition is read-only except through explicit Change Definition.
- [ ] unchanged Draft Release requires APPROVE but not EDIT.
- [ ] changed Draft direct Release requires EDIT + APPROVE.
- [ ] New direct Release requires ADD + APPROVE.
- [ ] legacy `ReleaseAsync(...)` is blocked server-side with `WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED`.
- [ ] `UpdateDraftHeaderAsync` rejects a persisted snapshot whose recomputed hash does not match the stored hash.
- [ ] `UpdateAndReleaseAsync` validates RowVersion + SnapshotRevision + SnapshotHash + SourceProductDefinitionRevisionId before mutation.
- [ ] shared header/release helpers never commit or rollback their caller's transaction.
- [ ] no inline UI/help message incorrectly tells users Save Draft is required before Release.
- [ ] deployment preflight confirms `CK_PrWorkOrder_SnapshotFormat` accepts format 3.


---

# 31. Prohibited shortcuts

The Code Agent must **not** implement any of these:

```text
Razor: SaveDraftAsync(); then ReleaseAsync();
```

```text
Razor: SaveDraftAsync(); then RefreshDefinition(); then Release();
```

```text
Release automatically rebuilds from latest Product Definition
```

```text
Disable readiness validation to make Release easier
```

```text
Ignore rowversion/snapshot fingerprint for existing Draft
```

```text
Catch Release failure after Create and leave the newly-created Draft
```

```text
Always refresh Product Definition before Release
```

```text
Delete/replace audit history
```

```text
Remove legacy snapshot protection
```

```text
Allow Product or Source Type to remain editable on a saved Draft while silently ignoring those screen values
```

```text
Recompute and overwrite SnapshotHash on an existing Draft before first verifying the persisted snapshot's current hash
```

```text
Require EDIT permission for every unchanged Draft Release
```

```text
Let a shared helper Commit/Rollback the transaction owned by CreateAndReleaseAsync or UpdateAndReleaseAsync
```


Those approaches either preserve the bad UX or weaken production integrity.

---

# 32. Final approved workflow

## New Work Order

```text
Product
Definition
Quantity
Schedule
       │
       ├──────── SAVE DRAFT
       │
       └──────── RELEASE
```

### SAVE DRAFT

```text
Build current snapshot
Persist Draft
```

### RELEASE

```text
Build current snapshot
Create Work Order
Validate readiness/schedule
Release
Commit atomically
```

## Existing current Draft

```text
Product             LOCKED
Definition          LOCKED (Change Definition command only)
Source Type         LOCKED

Edit Qty / Schedule / Reference / Remark
       │
       ├──────── SAVE DRAFT
       │
       └──────── RELEASE
```

Release validates the saved snapshot fingerprint/integrity, applies those permitted normal header changes, recalculates where required, and releases atomically.

## Definition structural change

```text
MORE
 ├─ Calculate Preview
 ├─ Recalculate Schedule
 ├─ Change Definition
 └─ Update from Product Definition
```

These are advanced/exceptional operations and are not mandatory steps in ordinary Work Order creation.

---

# 33. Approval

**Architecture:** APPROVED  
**ERP UX:** APPROVED  
**Saved identity contract:** LOCKED / APPROVED  
**Snapshot integrity:** PRESERVED + PRE-MUTATION VALIDATION REQUIRED  
**Legacy release protection:** SERVER-SIDE + UI ENFORCED  
**Permission model:** EXACT ADD / EDIT / APPROVE MATRIX DEFINED  
**Concurrency:** PRESERVED  
**Transaction ownership:** EXPLICIT / APPROVED  
**Auditability:** PRESERVED  
**Product Definition history:** PRESERVED  
**Database migration:** NO NEW MIGRATION REQUIRED; EXISTING V3 CONSTRAINT MUST BE VERIFIED  
**Implementation readiness:** **10/10**

This plan is intentionally aligned to the current `production` branch implementation at reviewed commit `ae35b23d7c8853d89d2a3fa5f44313f12f524b6e`. The Code Agent should implement against the existing Work Order snapshot builder, readiness validator, scheduler, audit model and SQL Server concurrency infrastructure rather than introducing a parallel workflow.
