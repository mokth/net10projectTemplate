# Costing-Safe Transaction Delete & Historical Archive
## APPROVED 10/10 Implementation Plan for Grok 4.7 Agent

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified production HEAD:** `26f2f836138898324c4c909fdbe53f51f5e3c192`  
**Plan status:** **APPROVED FOR IMPLEMENTATION**  
**Priority:** **MUST HAVE / COSTING CRITICAL**  
**Primary invariant:** **True drafts may disappear. Historical execution must not.**

**Repository re-review:** 2026-10-06 — current `production` still resolves exactly to `26f2f836138898324c4c909fdbe53f51f5e3c192` (0 commits ahead / 0 behind versus this plan's verified HEAD).

**Final review correction:** the plan remains approved only with §2.8 below. The current branch allows rollback-to-`NEW` documents to be edited/rebuilt, and some shared helpers physically remove owned `SP`/`CR`/`VR` batch headers during those rebuilds. Hardening Delete must preserve historical batch identity **without breaking rollback → edit → repost**.

---

# 0. Agent instruction

Implement this plan against the verified `production` branch only.

Before changing code:

1. Re-read every referenced source file in the current branch.
2. Preserve existing posting, rollback, costing, FIFO, production, e-Invoice, document-numbering, and concurrency behavior unless this plan explicitly changes it.
3. Do not invent new lifecycle states such as `DELETED`.
4. Do not physically delete any source, stock, valuation, production, or costing evidence that has ever participated in a completed or attempted posting.
5. Do not use `Status == NEW` as proof that a document has never posted.
6. Do not use an EF global query filter for archived rows.
7. Do not introduce a generic lock order that conflicts with the module's existing Post/Rollback lock order.
8. This repository uses checked-in SQL deployment scripts. Do **not** add EF Core migration classes unless the repository structure has changed after the verified commit.
9. Run focused tests first, then the complete relevant Inventory/Sales/Procurement/Production/Costing suites.
10. If implementation evidence conflicts with this plan because the branch changed, stop that specific change, document the new evidence, and adapt only the affected item.

---

# 1. Why this change is required

The current production branch has a real lifecycle ambiguity.

Many Delete flows physically delete documents when:

```text
Status == NEW
```

But several Rollback flows also return a previously posted document to `NEW`.

Verified examples include:

- `IvInventoryPostingService`: rollback returns `IvTrxBatch.BatchStatus` to `NEW` and increments `RollbackCount`.
- `SaInvoiceService`: rollback returns `SaInvoice.Status` to `NEW` and stamps `RollbackDate`.
- `SaDoService`: rollback returns `SaDo.Status` to `NEW` and stamps `RollbackDate`.
- `PoInvoiceService`: rollback returns `PoInvoice.Status` to `NEW`, increments `CostingRevision`, and stamps `RollbackDate`.
- `PoCdnService`: rollback returns `PoCdn.Status` to `NEW` and can increment `CostingRevision`.
- `SaCdnService`: rollback returns `SaCdn.Status` to `NEW`, increments `CostingRevision`, and can retain the CR stock batch/history.
- Production Material Issue rollback returns the underlying IP inventory batch to `NEW`.

Therefore:

```text
NEW
```

can mean:

```text
A. never posted
```

or:

```text
B. posted previously, then rolled back
```

These states must not share the same physical-delete behavior.

The required rule is:

> A document that has never participated in execution may be physically deleted.  
> A document that has any historical execution evidence may only be archived after all active effects are fully reversed.

---

# 2. Important corrections to the original plan

The original plan had the correct business goal, but the following corrections are mandatory for this repository.

## 2.1 Owner document and physical costing source are not always the same thing

Do **not** model deletion around one generic `TransactionIdentity` and assume it maps directly to one `StockPosting`.

Examples:

### Sales DO / Sales Invoice

The Sales document owns the business lifecycle, but stock posting runs through an inventory `SP` batch.

`InventoryStockPostingCommandFactory` creates the ledger identity from the **inventory batch**:

```text
SourceModule       = INVENTORY
SourceDocumentType = SP
SourceDocumentId   = IvTrxBatch.Id
SourceDocumentNo   = IvTrxBatch.BatchNo
```

The DO/Invoice number is connected through `RefNo` / inventory history / existing ownership resolver logic.

Therefore deleting a Sales document must evaluate:

```text
Business owner
+
owned SP batch
+
SP StockPosting history
+
IvTrxHistory
+
Sales dependencies
```

### Sales CN

`SaCdnService` may own a `CR` inventory batch when `ReturnStock = true`.

The archive plan must therefore include `SaCdn`, not only `SaInvoice` and `SaDo`.

### Purchase CN/DN

`PoCdnService` can have both:

```text
direct procurement costing posting: PO_CDN
+
owned VR inventory batch when ReturnStock = true
```

Both evidence sources must be checked.

### Purchase Invoice

`PoInvoiceService` has direct procurement costing ledger identity:

```text
SourceModule       = PROCUREMENT
SourceDocumentType = PO_INVOICE / PO_INVOICE_CN
SourceDocumentId   = DocNo
SourceDocumentNo   = DocNo
DocumentRevision   = CostingRevision
```

### Production

Production Output and Finished Good Receipt post directly using Production source identities, while Material Issue is tied to the IP inventory batch plus `ProductionPostingLink` and immutable movement rows.

**Conclusion:** the central policy must evaluate an **owner subject with zero or more execution sources**, not one guessed ledger identity.

---

## 2.2 Production rollback states are not uniform

Do not force Production into the Sales/Inventory `POSTED -> NEW` model.

Current behavior:

```text
Production Material Issue:
POSTED -> rollback -> underlying batch NEW

Daily Production / ProductionOutput:
POSTED -> rollback -> REVERSED

Finished Good Receipt:
POSTED -> rollback -> REVERSED
```

Delete behavior must respect these existing states.

---

## 2.3 This repo uses SQL scripts, not EF migration classes

The verified branch contains checked-in schema scripts such as:

```text
scripts/alter-ivtrxbatch-posting-audit.sql
scripts/alter-production-material-issue-draft.sql
scripts/alter-production-stock-ledger.sql
scripts/create-stock-posting-ledger.sql
```

There is no normal EF Core migrations folder in the verified branch.

Create an idempotent SQL deployment script for archive fields.

Recommended name:

```text
scripts/alter-transaction-delete-archive.sql
```

Update entity classes/configurations to match it.

---

## 2.4 Archiving a rolled-back NEW batch must release operational reservation behavior

This is critical.

Several operational queries treat:

```text
BatchStatus == NEW
```

as an active reservation.

Examples include:

- `IvSpShipmentService.SumOtherNewSpReservationsAsync`
- Material Issue draft reservation logic
- Material Issue draft queries

If a rolled-back historical batch remains `NEW` and is merely stamped `DeletedAtUtc`, but these queries do not exclude archived rows, stock can remain falsely reserved.

Therefore every "active NEW draft/reservation" query must also require:

```text
DeletedAtUtc == null
```

This is mandatory.

---

## 2.5 Do not use `HistoryExistsForBatchAsync` as "ever posted" evidence

`IvStockPostingRepository.HistoryExistsForBatchAsync` reduces history to the current generation.

That is useful for operational history, but it is not authoritative for:

```text
Has this batch ever executed?
```

A fully reversed V2 posting may have no active current generation even though immutable original/reversal evidence exists.

For delete safety use:

```text
ANY IvTrxHistory row ever for this tenant + batch
```

plus:

```text
ANY matching StockPosting row
```

Do not use only the current-generation helper.

---

## 2.6 Do not use EF global query filters

Costing Center, repair ownership, trace, audit, and source navigation must be able to resolve archived source rows.

Operational screens should explicitly exclude archives.

Historical/costing screens should deliberately include them.

---

## 2.7 Lock order must follow each module's existing Post/Rollback order

The original generic proposal:

```text
Branch lock -> source row lock
```

must **not** be blindly applied everywhere.

Some current Sales/Procurement flows lock the owner document before entering the Stock Ledger coordinator, which then acquires the branch stock lock.

Introducing the reverse order only in Delete can create a deadlock cycle.

Required rule:

> The policy service does not acquire locks.  
> Each caller evaluates delete safety inside its existing DB transaction after taking locks in the same order used by that module's Post/Rollback flow.

Inventory and Production paths that are already branch-lock-first should remain branch-lock-first.

Sales and Procurement must preserve their established document lock ordering unless Post/Rollback are changed in the same patch to the same new order.

---

## 2.8 Historical owned-batch identity must also survive rollback edit/rebuild

This is the final repository re-review correction and is **mandatory**.

The current branch does not only physically remove `NEW` batches from Delete flows. It can also remove them while a rolled-back document is being edited/rebuilt:

```text
SaDoService.UpdateAsync
SaInvoiceService.UpdateAsync
    -> IvSpShipmentService.ReleaseShipmentReservationAsync(..., removeBatch: true)

IvSpShipmentService.CreateOrReplaceShipmentAsync
    -> when required shipment lines become zero, it can remove the SP batch

SaCdnService / PoCdnService
    -> draft/rebuild paths can call DeleteNewStockInBatchInTransactionAsync(...)
       when ReturnStock becomes false or no stock-return lines remain
```

After rollback these owned batches can be `NEW` **and still be historical Stock Ledger sources**.

Therefore Phase 3 must not simply change the low-level delete helper to throw whenever history exists. That would protect history but break the legitimate ERP workflow:

```text
POST -> ROLLBACK -> EDIT -> REBUILD RESERVATION/RETURN -> REPOST
```

Required invariant:

> Once an owned physical inventory batch has participated in execution, its **header identity** is permanent historical identity for that owner. A rollback edit may rebuild the current draft/reservation detail state, but it must never physically delete or re-key that historical batch header.

For historical `SP` / `CR` / `VR` / `IP` sources:

```text
preserve IvTrxBatch.Id
preserve BatchNo
preserve TrxType
preserve owner/reference relationship needed by source resolution
preserve every StockPosting / IvTrxHistory / valuation / allocation / movement row
```

Rollback edit/rebuild rules:

1. **SP — Sales DO / Invoice**
   - If the owned SP has never executed, existing true-draft behavior may remove the batch header.
   - If the SP has historical execution, `ReleaseShipmentReservationAsync(..., removeBatch: true)` must **not** remove the header.
   - It may release/replace the current `NEW` reservation detail set only where the existing edit workflow already permits that mutation.
   - Subsequent `CreateOrReplaceShipmentAsync` must reuse the same historical batch header identity.
   - The `required.Count == 0` path must leave a historical header inert instead of deleting it.

2. **CR — Sales CN**
   - If rollback returns the CR to `NEW`, changing `ReturnStock` or stock-return lines must not call a physical header delete when the CR has historical execution.
   - Rebuild current draft details in place, or leave the historical header inert with zero active draft reservation details.
   - Re-enable/repost should reuse that same physical source identity where the existing resolver model expects one batch per document reference.

3. **VR — Purchase CN/DN**
   - Apply the same rule as CR.
   - `EnsureVrDraft...`, `RemoveUnexpectedVr...`, Post preparation, and Delete must distinguish **true-draft VR** from **historical rolled-back VR**.
   - Never remove the historical VR header merely because `ReturnStock` became false or stock lines became empty.

4. **IP — Production Material Issue**
   - Preserve the existing batch header and posting-link identity across rollback editing.
   - Historical material/balance-lot movements remain immutable.
   - Draft detail rebuilding may continue only through the existing supported workflow and must not remove immutable movement evidence.

5. **Do not create duplicate same-reference physical batches as a shortcut.**
   Current lock/resolver code generally expects one active physical batch for an owner reference. Prefer preserving and reusing the historical header. Only introduce a superseding-batch model if repository constraints are reworked consistently and dedicated tests prove source resolution, lock lookup, and Costing Center ownership remain deterministic. That redesign is **out of scope for this change**.

6. **Historical detail mutation boundary**
   - Immutable evidence is `StockPosting`, `IvTrxHistory`, valuation facts, cost allocations/settlements, and production movement/fact tables.
   - Current draft/reservation detail rows may be replaced only when the existing rollback-edit workflow already treats them as mutable.
   - Before removing any such detail row, confirm no immutable row has a restrictive FK/reference to that physical detail ID. If one does, retain/version the detail instead of deleting it.

7. **Low-level evidence helper must be reusable.**
   Implement one fail-safe batch-history probe used by both Delete and rebuild paths. It must answer:

```text
Has this physical IvTrxBatch ever executed?
```

using at least:

```text
ANY IvTrxHistory row for tenant + BatchNo
OR
ANY StockPosting row for INVENTORY + physical batch identity
OR
module-specific immutable movement/fact evidence where applicable
```

Do not use `HistoryExistsForBatchAsync` for this question because that helper returns only the current unreversed generation.

This correction is required so the implementation protects historical source identity **and** keeps rollback correction workflows usable.

---

# 3. Target user behavior

The user still clicks one normal action:

```text
Delete
```

The system decides internally whether that means:

```text
Hard delete a true draft
```

or:

```text
Archive a fully reversed historical transaction
```

The user should not need to understand "soft delete".

## 3.1 True draft message

Example:

```text
Delete MR000123?
```

Result:

```text
The draft is physically removed.
```

## 3.2 Historical rolled-back message

Example:

```text
Delete GR000123?

This document was previously posted and rolled back.
It will be removed from normal transaction lists while its
posting history is retained for audit and costing.
```

Result:

```text
Source document is archived.
Ledger/history remains unchanged.
```

---

# 4. Authoritative decision matrix

| Current lifecycle | Historical execution | Active posting/effect | Delete result |
|---|---:|---:|---|
| NEW true draft | No | No | `HardDeleteDraft` |
| NEW after rollback | Yes | No | `ArchiveHistorical` |
| REVERSED production document | Yes | No | `ArchiveHistorical` |
| POSTED | Yes | Yes | Block: rollback first |
| CLOSED / Force Closed | Yes | Yes or tombstoned | Block |
| Any | Unsealed/incomplete posting exists | Unknown | Block and route to Costing Center |
| Any | Active downstream dependency | Any | Block |
| Already archived | Historical | No | Idempotent success / already archived |

`NEW` by itself is never enough to authorize physical delete.

---

# 5. Core contracts

Create:

```text
ErpWeb.Core/Transactions/TransactionDeleteContracts.cs
ErpWeb.Core/Transactions/TransactionDeletePolicyService.cs
ErpWeb.Core/Transactions/TransactionLifecycleGuard.cs
```

Register the service in:

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

## 5.1 Owner types

Use explicit owner types.

Example:

```csharp
public static class TransactionDeleteOwnerTypes
{
    public const string InventoryBatch = "INVENTORY_BATCH";
    public const string SalesInvoice = "SA_INVOICE";
    public const string SalesDeliveryOrder = "SA_DO";
    public const string SalesCdn = "SA_CDN";
    public const string PurchaseInvoice = "PO_INVOICE";
    public const string PurchaseCdn = "PO_CDN";
    public const string ProductionMaterialIssue = "PRODUCTION_MATERIAL_ISSUE";
    public const string ProductionOutput = "PRODUCTION_OUTPUT";
    public const string ProductionFinishedGood = "PRODUCTION_FINISHED_GOOD";
}
```

## 5.2 Delete subject

```csharp
public sealed record TransactionDeleteSubject(
    string CompanyCode,
    string BranchCode,
    string OwnerType,
    string OwnerDocumentId,
    string OwnerDocumentNo);
```

Do not put guessed `SourceModule/SourceDocumentType` fields in the public subject.

The policy resolves execution sources from actual repository relationships.

## 5.3 Execution source identity

Internal contract:

```csharp
internal sealed record TransactionExecutionSource(
    string SourceModule,
    string SourceDocumentType,
    string SourceDocumentId,
    string SourceDocumentNo,
    int? PhysicalBatchNo = null);
```

One owner can produce multiple execution sources.

Example:

```text
PoCdn
  -> PROCUREMENT / PO_CDN / PCN0001
  -> INVENTORY / VR / IvTrxBatch.Id / BatchNo
```

## 5.4 Decision result

```csharp
public enum TransactionDeleteMode
{
    HardDeleteDraft,
    ArchiveHistorical,
    Block
}

public sealed record TransactionDeleteDecision(
    TransactionDeleteMode Mode,
    bool HasHistoricalPosting,
    bool HasActivePosting,
    bool HasUnsealedPosting,
    bool HasHistoricalMovement,
    bool HasDownstreamDependency,
    string? BlockingReason);
```

## 5.5 Policy interface

```csharp
public interface ITransactionDeletePolicyService
{
    Task<TransactionDeleteDecision> EvaluateAsync(
        AppDbContext db,
        TransactionDeleteSubject subject,
        CancellationToken cancellationToken = default);
}
```

The caller supplies its existing `AppDbContext`.

The evaluator must not:

- create a new DbContext,
- start/commit/rollback a transaction,
- acquire branch locks,
- mutate source data,
- alter StockPosting,
- alter valuation facts.

---

# 6. Historical evidence rules

## 6.1 StockPosting is primary ledger evidence

For every resolved execution source, inspect all `StockPosting` rows matching:

```text
CompanyCode
BranchCode
SourceModule
SourceDocumentType
SourceDocumentId
```

Use `SourceDocumentNo` as additional validation/display evidence, not as the only key.

## 6.2 Historical posting

```text
HasHistoricalPosting = any matching StockPosting row
```

A sealed PRIMARY and its sealed REVERSAL are both historical evidence.

## 6.3 Unsealed posting

```text
HasUnsealedPosting =
    any matching StockPosting where SealedAtUtc == null
```

Delete mode:

```text
Block
```

Message:

```text
This document has an incomplete stock/costing posting.
Use Costing Center reconciliation before deleting it.
```

## 6.4 Active posting

A sealed PRIMARY is active when no sealed reversal references it.

Concept:

```text
PRIMARY #100 sealed
REVERSAL #120 sealed, ReversesPostingId = 100
```

means:

```text
#100 historical = yes
#100 active     = no
```

Across repost cycles, **any** sealed PRIMARY without a sealed reversal means:

```text
HasActivePosting = true
Delete = Block
```

Do not assume the latest revision is enough.

---

# 7. Module-specific execution source resolution

Implement these rules inside the policy, preferably via private resolver methods or small internal adapters.

Do not create unnecessary public abstractions.

## 7.1 Inventory transaction owner

Owner:

```text
IvTrxBatch
```

Execution source is the batch itself:

```text
SourceModule       = INVENTORY
SourceDocumentType = batch.TrxType
SourceDocumentId   = batch.Id.ToString()
SourceDocumentNo   = batch.BatchNo.ToString()
```

This matches `InventoryStockPostingCommandFactory`.

Also check:

```text
all IvTrxHistory rows for tenant + BatchNo
```

not just current-generation history.

---

## 7.2 Sales Delivery Order

Owner:

```text
SaDo
```

Resolve the owned `SP` batch using the existing Sales shipment reference rules.

Do **not** rebuild the reference format independently.

Reuse the same helpers/patterns already used by:

```text
SaDoService
IvSpShipmentService
SaDoSpRefs
```

Evidence:

```text
SaDo lifecycle
owned SP batch
SP StockPosting rows
all SP IvTrxHistory rows
existing downstream Sales document dependencies
force-close state
```

---

## 7.3 Sales Invoice

Owner:

```text
SaInvoice
```

Resolve its owned/direct-stock `SP` batch using current `SaInvoiceService` shipment logic.

Evidence:

```text
SaInvoice lifecycle
e-Invoice structural lock
credit-note blocker
owned SP batch if present
SP StockPosting rows
all SP IvTrxHistory rows
SalesReturnCostAllocation / COGS lineage where applicable
```

An invoice with only linked DO stock may not own a direct stock batch. The policy must follow the actual current document/line ownership; absence of an SP batch is not automatically an error.

---

## 7.4 Sales CN/DN

Add archive support to:

```text
ErpWeb.Model/Entities/Sales/SaCdn.cs
ErpWeb.Core/Sales/SaCdnService.cs
ErpWeb.Model/Repositories/Sales/SaCdnRepository.cs
```

For CN with:

```text
ReturnStock = true
```

resolve the owned `CR` batch through the existing:

```text
SaCdnCrLock
CN/... RefNo
```

logic.

Historical evidence includes:

```text
CostingRevision
RollbackDate
CR StockPosting
all CR IvTrxHistory rows
SalesReturnCostAllocation
SalesReturnStandardCostVariance
```

For non-stock CN/DN, direct stock history may not exist; document lifecycle/e-Invoice/downstream evidence still applies.

---

## 7.5 Purchase Invoice

Owner:

```text
PoInvoice
```

Direct costing source:

```text
SourceModule       = PROCUREMENT
SourceDocumentType = PO_INVOICE
                   or PO_INVOICE_CN
SourceDocumentId   = DocNo
SourceDocumentNo   = DocNo
```

Historical evidence also includes:

```text
CostingRevision > 0
PurchaseReceiptCostSettlement
PurchaseCostAdjustment
```

`CostingRevision > 0` is strong evidence but must not replace the StockPosting check.

---

## 7.6 Purchase CN/DN

Owner:

```text
PoCdn
```

Direct costing source:

```text
PROCUREMENT / PO_CDN / DocNo
```

When `ReturnStock = true`, also resolve the owned `VR` batch through existing:

```text
PoCdnVrLock
PoCdnSpRefs
VrBatchNo
```

Evidence is the union of:

```text
direct PO_CDN StockPosting
owned VR StockPosting
all VR IvTrxHistory rows
PurchaseReceiptCostSettlement / PurchaseCostAdjustment as applicable
CostingRevision
```

---

## 7.7 Production Material Issue

Owner is the Material Issue execution represented by:

```text
IvTrxBatch TrxType = IP
ProductionPostingLink
ProductionMaterialIssueLine
```

Evidence includes:

```text
IP StockPosting
all IP IvTrxHistory rows
ProductionPostingLink rows
ProductionMaterialMovement rows
ProductionBalLotMovement rows
```

A rolled-back IP returns the batch to `NEW`; this must archive, not hard delete.

The existing safety idea in `ProductionMaterialIssueService.Lifecycle.cs` that checks for prior material movements must be retained and expanded into the common historical decision.

---

## 7.8 Daily Production / ProductionOutput

Owner:

```text
ProductionOutput
```

Direct Stock Ledger source:

```text
SourceModule       = PRODUCTION
SourceDocumentType = ProductionDocumentTypes.ProductionOutput
SourceDocumentId   = ProductionOutput.Uid
SourceDocumentNo   = ProductionOutput.DocumentNo
```

Evidence includes:

```text
ProductionPostingLink
ProductionMaterialMovement
ProductionBalLotMovement
StockPosting
StockValuationFact
ProductionPoolDependency / valuation evidence as applicable
```

Lifecycle:

```text
NEW      + no execution -> hard delete
POSTED                 -> block
REVERSED + history     -> archive
```

---

## 7.9 Finished Good Receipt

Owner:

```text
ProductionFinishedGoodReceipt
```

Direct Stock Ledger source:

```text
SourceModule       = PRODUCTION
SourceDocumentType = FinishedGoodReceipt
SourceDocumentId   = BatchId
SourceDocumentNo   = Batch.BatchNo
```

Evidence includes:

```text
PostingId
ReversalPostingId
ProductionPostingLink
ProductionFinishedGoodFact
ProductionFinishedGoodPriceSnapshot
ProductionBalLotMovement
IvTrxHistory
StockPosting
StockValuationFact
```

Lifecycle:

```text
NEW      + never posted -> hard delete
POSTED                  -> block
REVERSED                -> archive
```

---

# 8. Archive schema

Add archive metadata to the business owners that can remain after historical delete.

## 8.1 Inventory

```text
ErpWeb.Model/Entities/Inventory/IvTrxBatch.cs
ErpWeb.Model/Configurations/Inventory/IvTrxBatchConfiguration.cs
dbo.IvTrxBatch
```

Fields:

```csharp
public DateTime? DeletedAtUtc { get; set; }
public string? DeletedBy { get; set; }
public string? DeleteReason { get; set; }
public bool IsDeleted => DeletedAtUtc.HasValue;
```

Use the same audit-user width as the existing inventory audit columns:

```text
DeletedBy max length = 10
DeleteReason max length = 250
```

---

## 8.2 Sales

Add to:

```text
SaInvoice
SaDo
SaCdn
```

Use the module's existing user-id width:

```text
DeletedBy max length = 20
DeleteReason max length = 250
```

---

## 8.3 Procurement

Add to:

```text
PoInvoice
PoCdn
```

Use:

```text
DeletedBy max length = 20
DeleteReason max length = 250
```

---

## 8.4 Production

Add to:

```text
ProductionOutput / dbo.PrProductionOutput
ProductionFinishedGoodReceipt / dbo.PrFinishedGoodReceipt
```

For Material Issue, `IvTrxBatch` is the archival owner marker, so a new archive column on `ProductionPostingLink` is not required in the first implementation.

Use the existing production audit-user width where appropriate:

```text
DeletedBy max length = 10
DeleteReason max length = 250
```

For Finished Good Receipt, preserve its existing restrictive foreign keys.

---

# 9. SQL deployment script

Create:

```text
scripts/alter-transaction-delete-archive.sql
```

Requirements:

1. Idempotent `COL_LENGTH` checks.
2. `SET XACT_ABORT ON`.
3. Add nullable columns only.
4. Do not backfill existing rows as deleted.
5. Do not add cascading deletes to Stock Ledger or Production history.
6. Preserve existing keys and posting identities.
7. Add only indexes proven useful by operational queries.

Minimum columns:

```text
DeletedAtUtc datetime2 NULL
DeletedBy    module-appropriate varchar/nvarchar length
DeleteReason nvarchar(250) NULL
```

Affected SQL tables:

```text
dbo.IvTrxBatch
dbo.SaInvoice
dbo.SaDO
dbo.SaCDN
dbo.POInvoice
dbo.PoCdn
dbo.PrProductionOutput
dbo.PrFinishedGoodReceipt
```

Use the exact real table names and existing casing found in entity configuration/scripts.

Do not assume names from this plan without checking the current branch.

---

# 10. Hard-delete eligibility

`HardDeleteDraft` requires **all** applicable conditions.

## 10.1 Common

```text
not archived
operational lifecycle allows delete
no unsealed StockPosting
no sealed StockPosting
no direct financial costing posting
no immutable execution movement
no active downstream dependency
```

## 10.2 Inventory batch-specific

Additionally require:

```text
BatchStatus == NEW
PostedCount == 0
RollbackCount == 0
no IvTrxHistory row of any generation
no StockPosting row for the batch source identity
no linked production execution movement
```

`PostedDate`/`RollbackDate` are supporting evidence, not the sole authority.

## 10.3 Production-specific

Require no:

```text
ProductionPostingLink succeeded/reversal execution
ProductionMaterialMovement
ProductionBalLotMovement
FG fact/snapshot rows
```

for the candidate true draft.

---

# 11. Harden physical inventory batch deletion

Current helper:

```text
IvInventoryPostingService.DeleteNewStockInBatchInTransactionAsync(...)
```

is too weak because it only proves the batch is `NEW`.

Rename to:

```text
DeleteTrueDraftStockBatchInTransactionAsync(...)
```

or retain the old method temporarily as an obsolete wrapper that calls the hardened implementation.

Required checks:

```text
BatchStatus == NEW
DeletedAtUtc == null
PostedCount == 0
RollbackCount == 0
no StockPosting ever for INVENTORY + batch identity
no IvTrxHistory ever for Company/Branch/BatchNo
no production execution movement referencing the batch/detail
```

Failure message:

```text
This stock batch has historical execution evidence.
Archive the owning document instead of deleting its posting history.
```

This helper must never physically delete a rolled-back historical batch.

---

# 12. Harden other physical-delete helpers

## 12.1 `IvStockTransactionRepository.DeleteNewAsync`

This method is also status-only.

Search all current callers.

Preferred action:

```text
If unused in production:
    remove from public interface or mark obsolete and migrate tests/callers.

If used:
    rename/harden to true-draft semantics with the same historical checks.
```

Do not leave a second unsafe path.

## 12.2 `IvSpShipmentService.ReleaseShipmentReservationAsync`

Current `removeBatch: true` can physically remove a NEW SP batch.

For hard delete of a Sales true draft, this remains valid **only after** the delete decision proves the owned SP batch is a true draft.

For archived historical Sales documents:

```text
do not remove the SP batch
do not remove its historical details
```

Instead mark the owner and SP batch archived, then make reservation queries ignore archived batches.

For a **rolled-back historical Sales document that is still active and being edited/rebuilt**, do not archive the owner and do not physically remove/re-key the SP header. Preserve the historical `IvTrxBatch.Id`/`BatchNo` and release only the mutable current reservation detail set where safe. `CreateOrReplaceShipmentAsync` must reuse that header. The `required.Count == 0` path must keep a historical header inert rather than delete it. This is the §2.8 rollback-edit/repost rule.

---

# 13. Operational reservation rules after archive

This is a mandatory correctness gate.

Update every active-draft/reservation query so archived `NEW` rows do not reserve stock or production material.

At minimum inspect and update:

```text
ErpWeb.Core/Inventory/IvSpShipmentService.cs
    SumOtherNewSpReservationsAsync(...)
    SP NEW-batch reuse / allocation queries

ErpWeb.Core/Production/ProductionMaterialIssueDraftReservationReader.cs

ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs
    other draft calculations
    draft ownership/reuse queries

ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
```

Required predicate:

```text
batch.BatchStatus == NEW
AND batch.DeletedAtUtc == null
```

Do not delete retained historical detail lines merely to free reservation quantities.

---

# 14. Archive operation

When `ArchiveHistorical` is selected:

```text
DeletedAtUtc  = DateTime.UtcNow
DeletedBy     = current user truncated to the entity's configured audit width
DeleteReason  = validated user reason or a standard reason
ModifiedDate  = now where the entity has ModifiedDate
ModifiedBy    = current user where the entity has ModifiedBy
```

Do not change the operational status merely to represent deletion.

Keep:

```text
NEW
POSTED
REVERSED
CLOSED
```

as lifecycle states.

Archive state is:

```text
DeletedAtUtc != null
```

---

# 15. Owned physical batch archive rules

When a business owner has an owned physical inventory batch, archive them atomically in one transaction.

## 15.1 Sales DO / Invoice

If historical:

```text
archive SaDo/SaInvoice
archive owned SP IvTrxBatch
retain SP details
retain IvTrxHistory
retain StockPosting
retain StockValuationFact
retain FIFO/COGS lineage
```

## 15.2 Sales CN

If historical stock return:

```text
archive SaCdn
archive owned CR IvTrxBatch
retain CR details/history/valuation
```

## 15.3 Purchase CN/DN

If historical stock return:

```text
archive PoCdn
archive owned VR IvTrxBatch
retain VR details/history/valuation
retain direct PO_CDN costing history
```

## 15.4 Finished Good Receipt

Archive:

```text
PrFinishedGoodReceipt
owned IvTrxBatch
```

in one transaction.

Retain all restricted fact/snapshot/movement rows.

---

# 16. Write guard for archived sources

Implement:

```text
TransactionLifecycleGuard
```

Suggested overloads:

```csharp
public static void EnsureNotArchived(
    DateTime? deletedAtUtc,
    string documentLabel);

public static string? ArchivedError(
    DateTime? deletedAtUtc,
    string documentLabel);
```

Use the style that fits each existing service's result pattern.

Archived sources must be blocked from:

```text
Edit / Update
Post
Rollback
Force Close
Cancel that mutates execution
Create correction from the archived source unless explicitly read-only allowed
Submit e-Invoice
Create downstream transaction
Create/rebuild stock reservation
Cost settlement
Production execution
```

Message:

```text
This document was deleted after rollback and is retained for audit/costing history.
It cannot be edited or reposted.
```

Do not rely only on list filtering.

---

# 17. Inventory implementation

Update Delete flows in:

```text
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs
ErpWeb.Core/Inventory/IvMiscIssueService.cs
ErpWeb.Core/Inventory/IvStockAdjustmentService.cs
ErpWeb.Core/Inventory/IvStockTransferService.cs
ErpWeb.Core/Inventory/IvScrapService.cs
ErpWeb.Core/Inventory/IvStockReturnService.cs
ErpWeb.Core/Inventory/IvVendorReturnService.cs
```

Also inspect:

```text
IvStockTransactionRepository
IvInventoryPostingService
IvSpShipmentService
```

## 17.1 Required flow

Inside the service's existing transaction and lock order:

```text
lock candidate
validate type/tenant
if archived -> idempotent success
preserve module blockers
evaluate policy
```

Then:

```text
HardDeleteDraft
    -> delete details/header

ArchiveHistorical
    -> archive batch/header only
    -> retain all details/history/ledger evidence

Block
    -> rollback transaction
    -> return reason
```

For direct Inventory transactions, use the existing branch stock transaction lock in the same order used by posting.

---

# 18. Sales Delivery Order implementation

Update:

```text
ErpWeb.Core/Sales/SaDoService.cs
ErpWeb.Model/Repositories/Sales/SaDoRepository.cs
ErpWeb.Model/Entities/Sales/SaDo.cs
ErpWeb.Model/Configurations/Sales/SaDoConfiguration.cs
```

Preserve:

```text
RowVersion
shipment allocation rules
SO dependencies
billing dependencies
Force Close behavior
```

## True draft

Requirements:

```text
DO.Status == NEW
DO not archived
owned SP batch, if any, has never posted
no SP history
no active downstream reference
```

Action:

```text
release true-draft reservation
hard delete owned SP
hard delete DO details/header
```

## Rolled back

Requirements:

```text
DO.Status == NEW
historical SP execution exists
no active posting remains
```

Action:

```text
archive DO
archive owned SP batch
retain everything else
```

Never call the physical reservation delete helper on a historical SP batch.

---

# 19. Sales Invoice implementation

Update:

```text
ErpWeb.Core/Sales/SaInvoiceService.cs
ErpWeb.Model/Repositories/Sales/SaInvoiceRepository.cs
ErpWeb.Model/Entities/Sales/SaInvoice.cs
ErpWeb.Model/Configurations/Sales/SaInvoiceConfiguration.cs
```

Preserve existing blockers:

```text
e-Invoice lock
credit-note dependency
RowVersion
commercial/downstream rules
```

## True draft

If no historical owned/direct SP execution exists:

```text
release true-draft SP reservation
hard delete invoice
```

## Rolled back

If historical stock/COGS execution exists and is fully reversed:

```text
archive invoice
archive owned SP batch if it exists
retain StockPosting
retain StockValuationFact
retain Sales COGS / return allocations
```

A linked-DO invoice that does not own direct stock must not be falsely treated as having an SP batch.

---

# 20. Sales CN/DN implementation

This was missing from the original initial scope and is mandatory.

Update:

```text
ErpWeb.Core/Sales/SaCdnService.cs
ErpWeb.Model/Repositories/Sales/SaCdnRepository.cs
ErpWeb.Model/Entities/Sales/SaCdn.cs
ErpWeb.Model/Configurations/Sales/SaCdnConfiguration.cs
```

Preserve:

```text
e-Invoice lock
invoice remaining calculations
ReturnStock validation
RowVersion
CostingRevision
```

## Never-posted CN with draft CR

If:

```text
Status == NEW
CostingRevision == 0
CR batch has no StockPosting/history
```

then:

```text
hard delete true-draft CR
hard delete CN
```

## Previously posted/rolled-back CN

If:

```text
Status == NEW
CostingRevision > 0
or historical CR StockPosting/history exists
```

then:

```text
archive SaCdn
archive CR batch
retain all return-cost evidence
```

---

# 21. Purchase Invoice implementation

Update:

```text
ErpWeb.Core/Purchase/PoInvoiceService.cs
ErpWeb.Model/Repositories/Purchase/PoInvoiceRepository.cs
ErpWeb.Model/Entities/Purchase/PoInvoice.cs
ErpWeb.Model/Configurations/Purchase/PoInvoiceConfiguration.cs
```

## True draft

```text
Status == NEW
CostingRevision == 0
no direct procurement StockPosting
no settlement/adjustment execution
```

-> hard delete.

## Rolled back

```text
Status == NEW
CostingRevision > 0
or any direct procurement posting/settlement history
```

-> archive.

Retain:

```text
PurchaseReceiptCostSettlement
PurchaseCostAdjustment
StockPosting
StockValuationFact
```

---

# 22. Purchase CN/DN implementation

Update:

```text
ErpWeb.Core/Purchase/PoCdnService.cs
ErpWeb.Model/Repositories/Purchase/PoCdnRepository.cs
ErpWeb.Model/Entities/Purchase/PoCdn.cs
ErpWeb.Model/Configurations/Purchase/PoCdnConfiguration.cs
```

## True draft

Possible evidence set:

```text
PO_CDN direct financial source has never posted
owned VR is NEW and never posted
no historical VR IvTrxHistory
CostingRevision == 0
```

Action:

```text
hard delete VR true draft
hard delete PoCdn
```

## Rolled back

If either direct PO_CDN costing or VR has historical execution:

```text
archive PoCdn
archive VR batch if present
retain direct procurement posting
retain VR posting/history
retain purchase cost adjustments
```

---

# 23. Production Material Issue implementation

Update:

```text
ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs
ErpWeb.Core/Production/ProductionMaterialIssueDraftReservationReader.cs
```

## True draft

Current hard delete is acceptable only if additionally proven:

```text
batch NEW
link DRAFT
batch not archived
no StockPosting
no IvTrxHistory ever
no ProductionMaterialMovement
no ProductionBalLotMovement
```

Then delete:

```text
draft ProductionMaterialIssueLine rows
draft link
draft batch details
draft batch
```

## Rolled back historical IP

Current rollback returns:

```text
batch -> NEW
original posting link -> DRAFT
```

but immutable movement/reversal history remains.

Delete must therefore:

```text
archive IvTrxBatch
retain link
retain issue-line historical mapping
retain original and reversal movements
retain ledger/history
```

Update draft reservation queries to ignore archived batch.

---

# 24. Daily Production implementation

Update:

```text
ErpWeb.Core/Production/ProductionOutputService.cs
ErpWeb.Core/Production/ProductionOutputService.Entry.cs
ErpWeb.Core/Production/ProductionOutputService.Posting.cs
ErpWeb.Core/Production/ProductionOutputService.Rollback.cs
ErpWeb.Model/Entities/Production/ProductionOutput.cs
ErpWeb.Model/Configurations/Production/ProductionOutputConfiguration.cs
```

Current `DeleteAsync` physically removes any `NEW` output and its posting link.

Replace with:

```text
NEW + no historical execution
    -> hard delete

POSTED
    -> block, rollback first

REVERSED + fully reversed execution
    -> archive ProductionOutput
```

For archived REVERSED output retain:

```text
ProductionPostingLink
ProductionMaterialMovement
ProductionBalLotMovement
ProductionPoolDependency
valuation evidence
StockPosting
StockValuationFact
```

Do not physically remove the posting link of a historically executed output.

---

# 25. Finished Good Receipt implementation

Update:

```text
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs
ErpWeb.Model/Entities/Production/ProductionFinishedGoodReceipt.cs
ErpWeb.Model/Configurations/Production/ProductionFinishedGoodReceiptConfiguration.cs
```

Current `DeleteAsync` only checks:

```text
BatchStatus == NEW
```

Replace with policy decision.

## True draft

```text
NEW
PostingId == null
ReversalPostingId == null
no StockPosting
no IvTrxHistory
no FG fact/snapshot
```

-> physical delete is allowed.

## Posted

```text
POSTED
```

-> block; rollback first.

## Reversed

```text
REVERSED
historical posting fully reversed
```

-> archive:

```text
PrFinishedGoodReceipt
IvTrxBatch
```

Retain:

```text
PrFinishedGoodSource
PrFinishedGoodFact
PrFinishedGoodPriceSnapshot
PrFinishedGoodLotOrigin
ProductionBalLotMovement
IvTrxHistory
StockPosting
StockValuationFact
```

---

# 26. Operational list and picker filtering

All normal operational queries must exclude archived owners.

Required condition:

```text
DeletedAtUtc == null
```

Do not add a global EF filter.

At minimum inspect:

## Inventory

```text
IvStockTransactionRepository.SearchPagedAsync
inventory list services
inventory source/copy pickers
IvSpShipmentService active SP queries
```

## Sales

```text
SaInvoiceRepository
SaDoRepository
SaCdnRepository
transaction list pages
copy-from pickers
reservation calculations
invoice/CN remaining calculations where deleted rows must not count operationally
```

## Procurement

```text
PoInvoiceRepository
PoCdnRepository
transaction list pages
copy/reference pickers
credit-note remaining calculations
```

## Production

```text
ProductionOutputService.SearchAsync
ProductionOutput eligible/downstream queries where archived execution must not become active
Finished Good Receipt SearchAsync
Material Issue search/list/reservation queries
```

Archived historical rows remain queryable through explicit audit/costing paths.

---

# 27. Costing Center integration

Costing Center must continue to see archived historical evidence.

Do not add:

```text
DeletedAtUtc == null
```

to:

```text
CostingDiagnosticService
CostingTraceService
CostingRepairOwnershipResolver
Costing repair planners/services
Stock valuation queries
```

unless the query is explicitly an operational source picker.

Update:

```text
ErpWeb.UI/Inventory/Inquiry/CostingSourceNavigationResolver.cs
```

only as needed so navigation to an archived source still opens its existing view route.

The source page itself must render archived rows in read-only mode.

Banner:

```text
Deleted after rollback

This document is retained for costing and audit history.
It cannot be edited or reposted.
```

---

# 28. Missing source diagnostic

Add a new Costing Center finding code to:

```text
ErpWeb.Core/Costing/CostingContracts.cs
```

Use the next available `CD-xxx` code from the current branch; do not invent a conflicting number.

Semantic name:

```text
SourceDocumentMissing
```

Trigger when:

```text
sealed StockPosting exists
AND
authoritative owner/source resolver cannot resolve the physical or business source
AND
the row is not an accepted legacy/cutover exception
```

Severity:

```text
Critical
```

Repair target:

```text
DiagnosticOnly
```

Action:

```text
TraceSourceDocument
```

Explanation:

```text
A sealed stock/costing posting exists but its source document cannot be resolved.
Ledger evidence remains intact, but source-document auditability is incomplete.
```

Do not auto-recreate or auto-delete valuation facts.

---

# 29. Source snapshots remain authoritative

`StockPosting` already stores:

```text
SourceSnapshotJson
SourceSnapshotHash
SourceSnapshotSchemaVersion
```

This matters for:

```text
POST
ROLLBACK
EDIT NEW
DELETE / ARCHIVE
```

The archived source header may contain the final pre-delete state, while the `StockPosting.SourceSnapshotJson` records what was actually posted.

Costing/audit UI should treat:

```text
Posted snapshot
```

and:

```text
Last source-document state
```

as different concepts.

Do not overwrite the ledger snapshot during archive.

---

# 30. Zero-costing-delta invariant

Archive is a metadata lifecycle operation only.

For `ArchiveHistorical`, the transaction must not mutate:

```text
StockPosting
StockValuationFact
StockCostState
StockFifoLayer
StockFifoLayerConsumption
PurchaseReceiptCostSettlement
PurchaseCostAdjustment
SalesReturnCostAllocation
SalesReturnStandardCostVariance
ProductionStandardCostVariance
ProductionPoolDependency
Inventory balance quantity
Sales COGS
Production WIP/FG cost
Period snapshot valuation
```

Formal invariant:

> Historical source archive produces exactly zero stock quantity delta and zero monetary delta.

---

# 31. Document numbers

Do not recycle document numbers after:

```text
hard delete
```

or:

```text
archive
```

Existing numbering services should continue to advance normally.

No code should decrement/reset running numbers during delete.

Number gaps are valid and safer than reuse.

---

# 32. Concurrency and lock-order requirements

## 32.1 Policy service

The policy service:

```text
does not open a transaction
does not acquire locks
does not commit
does not mutate
```

It evaluates evidence within the caller's locked transaction.

## 32.2 Inventory

Use the existing Inventory order, including:

```text
BranchStockTransactionLock
then batch/source locks
```

where that is the current posting order.

## 32.3 Production

Preserve existing Production branch/work-order/source lock ordering.

Do not introduce an inverse lock sequence.

## 32.4 Sales / Procurement

Preserve the same owner-document and related-document ordering already used by Post/Rollback:

Examples include existing:

```text
SaCdnLockOrder
PoCdnLockOrder
owner repository LockForUpdateAsync
StockPostingCoordinator branch lock
```

Do not acquire BranchStockTransactionLock before an owner lock if the corresponding Post path does the opposite, unless the entire module is changed consistently in the same patch.

## 32.5 RowVersion

Keep existing optimistic concurrency checks for:

```text
SaInvoice
SaDo
SaCdn
PoInvoice
PoCdn
Production Output
Finished Good Receipt
```

The common policy does not replace RowVersion.

---

# 33. Delete reason

For historical archive, require a reason in the service contract or supply a standardized reason from the UI.

Recommended UI default:

```text
Deleted after rollback
```

Allow user explanation up to:

```text
250 characters
```

True draft hard delete does not need an audit reason unless the existing UI already asks for one.

---

# 34. Suggested implementation sequence

## Phase 1 — Schema + entities

1. Add `scripts/alter-transaction-delete-archive.sql`.
2. Add archive properties to entity classes.
3. Update entity configuration lengths/types.
4. Add focused schema/model tests.

Do not change delete behavior yet.

---

## Phase 2 — Core policy

Create:

```text
TransactionDeleteContracts.cs
TransactionDeletePolicyService.cs
TransactionLifecycleGuard.cs
```

Implement owner/execution-source resolution.

Register in:

```text
CoreServiceCollectionExtensions.cs
```

Add policy tests before wiring services.

---

## Phase 3 — Harden shared physical-delete paths

Harden:

```text
DeleteNewStockInBatchInTransactionAsync
IvStockTransactionRepository.DeleteNewAsync
IvSpShipmentService removeBatch path
```

Make unsafe historical physical delete impossible even if one caller forgets the high-level policy.

Also preserve rollback-edit-repost behavior: audit every non-Delete caller of these helpers. Historical `SP`/`CR`/`VR` batch headers must be retained and reused during draft rebuild; only never-posted true-draft headers may be physically removed. Add explicit handling for `IvSpShipmentService.CreateOrReplaceShipmentAsync` when `required.Count == 0`.

---

## Phase 4 — Inventory

Wire all Inventory DeleteAsync flows.

Add archived list filtering.

Verify no reservation behavior is affected.

---

## Phase 5 — Sales

Implement in this order:

```text
SaDo
SaInvoice
SaCdn
```

Then update:

```text
SP / CR reservation queries
Sales repositories
Sales transaction lists/pickers
```

---

## Phase 6 — Procurement

Implement:

```text
PoInvoice
PoCdn
owned VR behavior
```

Then update procurement lists/pickers/remaining calculations.

---

## Phase 7 — Production

Implement:

```text
Material Issue
Daily Production
Finished Good Receipt
```

Update production reservation and active execution queries.

---

## Phase 8 — Costing Center

Implement:

```text
archived source navigation
read-only archive display
SourceDocumentMissing diagnostic
```

Verify archived sources remain visible to trace/repair ownership.

---

## Phase 9 — Regression

Run all focused + cross-module costing tests.

Only mark complete after the zero-delta assertions pass.

---

# 35. Required policy tests

Create a focused test class, for example:

```text
ErpWeb.Tests/Other/TransactionDeletePolicyTests.cs
```

or use module-grouped folders consistent with the current test structure.

Required cases:

```text
No history -> HardDeleteDraft
Sealed PRIMARY active -> Block
Sealed PRIMARY + sealed REVERSAL -> ArchiveHistorical
Unsealed PRIMARY -> Block
Multiple generations, all reversed -> ArchiveHistorical
Multiple generations, latest active -> Block
IvTrxHistory exists but StockPosting missing -> ArchiveHistorical / fail-safe historical
Production movement exists but ledger source missing -> ArchiveHistorical / Block if inconsistent
Already archived -> idempotent archived result
```

---

# 36. Inventory tests

For each supported Inventory transaction type where practical:

```text
NEW never posted -> Delete -> physical rows removed

POSTED -> Delete -> blocked

POST
ROLLBACK
Delete
    -> source archived
    -> details retained
    -> IvTrxHistory retained
    -> StockPosting retained
    -> StockValuationFact retained
```

Minimum types:

```text
MR
GR / NG
MI
SC
TR
ADJ
CR
VR
```

Also test:

```text
rolled-back archived NEW batch does not count as active reservation
```

---

# 37. Sales tests

## SaDo

```text
NEW DO + never-posted SP -> hard delete both
POSTED DO -> blocked
POST -> ROLLBACK -> DELETE -> DO + SP archived
archived SP no longer reserves stock
archived DO cannot EDIT
archived DO cannot POST
POST -> ROLLBACK -> edit shipment identity -> historical SP header Id/BatchNo preserved
POST -> ROLLBACK -> edit to zero shipment-required lines -> historical SP header retained, no active reservation
rebuild/repost after rollback -> previous StockPosting source still resolves and new posting succeeds
```

## SaInvoice

```text
NEW invoice + true-draft direct SP -> hard delete
POST -> ROLLBACK -> DELETE -> archive
e-Invoice locked -> delete blocked
active CN dependency -> delete blocked
archived invoice cannot POST
POST -> ROLLBACK -> edit stock-line identity -> historical direct SP header preserved
POST -> ROLLBACK -> edit to zero direct-shipment requirement -> historical SP header retained, no active reservation
repost after rollback keeps historical source navigation valid
```

## SaCdn

```text
NEW CN ReturnStock=false, no history -> hard delete
NEW CN + true-draft CR -> hard delete CN + CR
POST CN + ROLLBACK + DELETE -> archive CN + CR
CostingRevision > 0 -> never hard delete
archived CR no longer acts as active operational batch
POST -> ROLLBACK -> ReturnStock true -> false does not delete historical CR header
POST -> ROLLBACK -> rebuild CR details in place -> old ledger/history preserved
ReturnStock re-enabled -> same physical CR source identity is reused unless repository evidence proves another deterministic model
```

---

# 38. Procurement tests

## PoInvoice

```text
NEW CostingRevision=0 no ledger -> hard delete
POSTED -> block
POST -> ROLLBACK -> DELETE -> archive
CostingRevision > 0 -> never physical delete
settlement/adjustment rows retained
```

## PoCdn

```text
NEW no return stock -> hard delete
NEW + true-draft VR -> hard delete both
POST/ROLLBACK with direct PO_CDN costing -> archive
POST/ROLLBACK with VR -> archive both
direct procurement ledger retained
VR ledger/history retained
POST -> ROLLBACK -> ReturnStock true -> false does not delete historical VR header
POST -> ROLLBACK -> zero stock-return lines leaves historical VR inert, not physically removed
ReturnStock re-enabled/repost reuses deterministic physical source identity and old StockPosting remains resolvable
```

---

# 39. Production tests

## Material Issue

```text
NEW IP never posted -> hard delete
POSTED -> block
POST -> ROLLBACK -> batch NEW -> DELETE -> archive
original/reversal ProductionMaterialMovement retained
archived IP no longer reserves material
```

## Daily Production

```text
NEW -> hard delete
POSTED -> block
POST -> ROLLBACK -> REVERSED -> DELETE -> archive
posting links retained
material/bal-lot movements retained
pool dependency/valuation evidence retained
```

## Finished Good Receipt

```text
NEW never posted -> hard delete
POSTED -> block
POST -> ROLLBACK -> REVERSED -> DELETE -> archive
FG facts retained
FG price snapshots retained
bal-lot movement retained
IvTrxHistory retained
StockPosting retained
StockValuationFact retained
```

---

# 40. Zero-delta regression assertions

For every archive test, capture BEFORE:

```text
StockPosting count + ids + SourceSnapshotHash
StockValuationFact count
signed valuation qty
signed valuation value
StockCostState qty/value/average
FIFO layer balances
FIFO consumption rows
PurchaseReceiptCostSettlement
PurchaseCostAdjustment
Sales COGS / return allocation totals
Inventory as-of valuation
Production cost totals
period snapshot inputs
```

Archive the source.

Capture AFTER.

Assert:

```text
BEFORE == AFTER
```

for every costing measure.

Allowed changes:

```text
DeletedAtUtc
DeletedBy
DeleteReason
normal ModifiedDate / ModifiedBy audit fields
```

Nothing else.

---

# 41. Reservation regression assertions

These tests are mandatory because the archived physical batch may still have `NEW` status.

After archiving rolled-back:

```text
SP batch
CR batch
VR batch where any reservation-like query applies
IP batch
```

verify:

```text
it is not counted as active reservation
it is not reused for a new operational document
it is not offered in a normal picker
it remains resolvable by audit/costing
```

---

# 42. Costing Center tests

After:

```text
POST
ROLLBACK
DELETE/ARCHIVE
```

verify:

```text
Costing Center still finds posting
trace shows PRIMARY and REVERSAL
repair ownership remains resolvable
source navigation opens archived source
archived source is view-only
net fully reversed valuation is still zero
no false missing-source finding
```

Then in a controlled test setup physically remove/corrupt a source reference and verify:

```text
SourceDocumentMissing
```

is raised as Critical.

---

# 43. Search checklist before completion

Search the production source for all patterns below and classify every hit.

```text
BatchStatus == IvBatchStatuses.New
Status == ...New
.Status == "NEW"
Remove(...)
RemoveRange(...)
DeleteNew
removeBatch: true
required.Count == 0
DeleteNewStockInBatchInTransactionAsync
LockSpBatchByRefAsync
LockByCnRefAsync
LockByVrRefAsync
ProductionPostingLinkStatuses.Draft
RollbackDate
RollbackCount
CostingRevision
RefNo == ...
IvTrxBatches
ProductionOutputs
ProductionFinishedGoodReceiptRows
```

For every operational "NEW means active" query, decide whether it must add:

```text
DeletedAtUtc == null
```

For every physical `Remove` path on a costing-relevant header/batch, prove it is true-draft-only.

No unexplained status-only physical delete may remain.

---

# 44. Files expected to change

This is the minimum expected set; Grok must add other files discovered by reference search.

## Core

```text
ErpWeb.Core/Transactions/TransactionDeleteContracts.cs          NEW
ErpWeb.Core/Transactions/TransactionDeletePolicyService.cs      NEW
ErpWeb.Core/Transactions/TransactionLifecycleGuard.cs           NEW
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

## Inventory

```text
ErpWeb.Model/Entities/Inventory/IvTrxBatch.cs
ErpWeb.Model/Configurations/Inventory/IvTrxBatchConfiguration.cs
ErpWeb.Model/Repositories/Inventory/IvStockTransactionRepository.cs
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs   if helper added
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
ErpWeb.Core/Inventory/IvSpShipmentService.cs
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs
ErpWeb.Core/Inventory/IvMiscIssueService.cs
ErpWeb.Core/Inventory/IvStockAdjustmentService.cs
ErpWeb.Core/Inventory/IvStockTransferService.cs
ErpWeb.Core/Inventory/IvScrapService.cs
ErpWeb.Core/Inventory/IvStockReturnService.cs
ErpWeb.Core/Inventory/IvVendorReturnService.cs
```

## Sales

```text
ErpWeb.Model/Entities/Sales/SaDo.cs
ErpWeb.Model/Entities/Sales/SaInvoice.cs
ErpWeb.Model/Entities/Sales/SaCdn.cs
ErpWeb.Model/Configurations/Sales/SaDoConfiguration.cs
ErpWeb.Model/Configurations/Sales/SaInvoiceConfiguration.cs
ErpWeb.Model/Configurations/Sales/SaCdnConfiguration.cs
ErpWeb.Model/Repositories/Sales/SaDoRepository.cs
ErpWeb.Model/Repositories/Sales/SaInvoiceRepository.cs
ErpWeb.Model/Repositories/Sales/SaCdnRepository.cs
ErpWeb.Core/Sales/SaDoService.cs
ErpWeb.Core/Sales/SaInvoiceService.cs
ErpWeb.Core/Sales/SaCdnService.cs
```

## Procurement

```text
ErpWeb.Model/Entities/Purchase/PoInvoice.cs
ErpWeb.Model/Entities/Purchase/PoCdn.cs
ErpWeb.Model/Configurations/Purchase/PoInvoiceConfiguration.cs
ErpWeb.Model/Configurations/Purchase/PoCdnConfiguration.cs
ErpWeb.Model/Repositories/Purchase/PoInvoiceRepository.cs
ErpWeb.Model/Repositories/Purchase/PoCdnRepository.cs
ErpWeb.Core/Purchase/PoInvoiceService.cs
ErpWeb.Core/Purchase/PoCdnService.cs
```

## Production

```text
ErpWeb.Model/Entities/Production/ProductionOutput.cs
ErpWeb.Model/Configurations/Production/ProductionOutputConfiguration.cs
ErpWeb.Model/Entities/Production/ProductionFinishedGoodReceipt.cs
ErpWeb.Model/Configurations/Production/ProductionFinishedGoodReceiptConfiguration.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs
ErpWeb.Core/Production/ProductionMaterialIssueDraftReservationReader.cs
ErpWeb.Core/Production/ProductionOutputService.cs
ErpWeb.Core/Production/ProductionOutputService.Entry.cs
ErpWeb.Core/Production/ProductionOutputService.Posting.cs
ErpWeb.Core/Production/ProductionOutputService.Rollback.cs
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs
```

## Costing / UI

```text
ErpWeb.Core/Costing/CostingContracts.cs
ErpWeb.Core/Costing/CostingDiagnosticService.cs
ErpWeb.Core/Costing/CostingRepairOwnershipResolver.cs   only if archived resolution needs adjustment
ErpWeb.UI/Inventory/Inquiry/CostingSourceNavigationResolver.cs
operational list/view components discovered by reference search
```

## SQL

```text
scripts/alter-transaction-delete-archive.sql            NEW
```

## Tests

Add/update grouped tests under:

```text
ErpWeb.Tests/Inventory/Transaction
ErpWeb.Tests/Sales/Transaction
ErpWeb.Tests/Procurement/Transaction
ErpWeb.Tests/Production/Transaction
ErpWeb.Tests/Inventory/Inquiry
ErpWeb.Tests/Other
```

---

# 45. Acceptance criteria

Implementation is approved only when **all** are true.

1. `NEW` alone never authorizes physical delete.
2. Never-posted drafts remain easy to delete.
3. Previously posted + fully rolled-back documents can be removed from normal user lists.
4. Historical delete is implemented as archive, not physical removal.
5. Sales DO/Invoice correctly evaluate owned `SP` batch history.
6. Sales CN correctly evaluates owned `CR` batch history.
7. Purchase CN/DN evaluates both direct `PO_CDN` costing and owned `VR` history.
8. Purchase Invoice evaluates its direct procurement costing history.
9. Material Issue rollback-to-NEW is never mistaken for a true draft.
10. Daily Production `REVERSED` can be archived, not hard deleted.
11. Finished Good Receipt `REVERSED` can be archived, not hard deleted.
12. Archived `NEW` physical batches do not reserve stock/material.
13. Archived documents cannot be edited/reposted.
14. No global query filter hides archived data from Costing Center.
15. StockPosting evidence is retained.
16. IvTrxHistory evidence is retained.
17. StockValuationFact evidence is retained.
18. FIFO lineage is unchanged by archive.
19. Sales COGS is unchanged by archive.
20. Purchase settlements/adjustments are unchanged by archive.
21. Production movement/cost lineage is unchanged by archive.
22. Period-close valuation is unchanged by archive.
23. Costing Center still traces archived sources.
24. Missing physical source is diagnosed as Critical.
25. Existing e-Invoice locks still work.
26. Existing RowVersion behavior still works.
27. Existing Force Close tombstones still work.
28. Delete/Post/Rollback races are serialized without introducing inverse lock order.
29. Document numbers are never recycled.
30. Full focused regression suite passes.
31. Rolled-back historical `SP`/`CR`/`VR` batch headers are never physically removed during edit/rebuild.
32. Hardening shared helpers does not break the supported `POST -> ROLLBACK -> EDIT -> REPOST` workflow.
33. Rebuild/repost keeps previous StockPosting source identities resolvable in Costing Center.
34. `IvSpShipmentService.CreateOrReplaceShipmentAsync` with zero required lines retains a historical SP header while releasing active reservation effect.
35. True-draft owned batches with no execution history can still be physically removed normally.

---

# 46. Definition of done for Grok 4.7 Agent

Do not report completion merely because the code builds.

The implementation is complete only after Grok reports:

```text
1. Exact files changed.
2. SQL script added.
3. All old unsafe physical-delete call sites found and resolved.
4. All active NEW reservation queries audited.
5. Unit/integration tests added.
6. Focused tests passed.
7. Costing zero-delta assertions passed.
8. Reservation-release assertions passed.
9. Costing Center archived-source trace passed.
10. No unresolved status-only hard-delete path remains.
11. Every non-Delete caller of shared physical-batch removal helpers was classified as true-draft-safe or changed to preserve historical header identity.
12. Rollback -> edit/rebuild -> repost regression tests pass for historical SP/CR/VR ownership.
```

If any unsafe path remains, status is:

```text
NOT APPROVED
```

---

# 47. Final architecture

```text
                           CREATE
                              |
                              v
                         +---------+
                         |   NEW   |
                         +----+----+
                              |
                  +-----------+------------+
                  |                        |
                DELETE                    POST
                  |                        |
                  v                        v
        Resolve owner + execution      POSTED
             evidence                    |
                  |                    ROLLBACK
        +---------+---------+             |
        |                   |             v
   no history          history exists   module-specific
        |                   |           rollback state
        v                   v          NEW or REVERSED
 HARD DELETE       active effect?             |
                         |                    DELETE
                   +-----+-----+               |
                   |           |               v
                  YES          NO      ARCHIVE HISTORICAL
                   |           |       source retained
                 BLOCK       ARCHIVE    ledger retained
```

For archived owned physical batches:

```text
status may still be NEW
BUT
DeletedAtUtc != null
AND
all operational reservation queries exclude it
```

---

# 48. Final rule

> **True drafts may disappear. Accounting, costing, stock, and production history must not.**

In this ERP:

```text
NEW + no historical execution evidence
```

means:

```text
safe to hard delete
```

while:

```text
NEW or REVERSED + historical execution evidence + fully reversed effects
```

means:

```text
archive only
```

and:

```text
any active or incomplete posting
```

means:

```text
block delete
```

This design preserves user simplicity while protecting:

```text
Stock Ledger V2
Moving Average
FIFO
Standard Cost
Sales COGS
Purchase cost settlement
Production WIP/FG costing
Period close
Costing Center
Audit trace
```

---

# 49. Approval

**Architecture completeness:** 10/10  
**Costing safety:** 10/10  
**Repository fit:** 10/10  
**User experience:** 10/10  
**Concurrency safety target:** 10/10, conditional on preserving existing module lock order  
**Implementation readiness for Grok 4.7 Agent:** **APPROVED**

**Repository re-review status:** **APPROVED WITH §2.8 INCLUDED** — current `production` is still exactly the verified commit, and the rollback-edit/rebuild source-identity gap has been closed in this final plan.

**Implementation directive:** Proceed in the phased order above. Do not shortcut the historical evidence, reservation-filter, zero-delta, or rollback-edit/repost identity-preservation requirements.
