# Production Rollback-to-NEW Standardization
## FINAL REPO-VERIFIED — APPROVED FOR IMPLEMENTATION — 10/10
### AI Coding Agent Plan — Costing + Quantity Integrity Critical
### Repository: `mokth/net10projectTemplate`
### Branch reviewed: `productionv2`
### Scope: IP, Daily Production, Production Stock Return, Production Stock Adjustment, Finished Good Receipt

---

# 0. Final approval

**STATUS: APPROVED FOR IMPLEMENTATION — 10/10**

This plan supersedes all earlier versions of the POSTED→NEW Production rollback plan.

This final review was performed again against the current `productionv2` code paths, especially:

- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`
- `ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs`
- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`
- `ProductionMaterialIssueService.*`
- `ProductionOutputService.*`
- `ProductionStockReturnService.*`
- `ProductionStockAdjustmentService.cs`
- `ProductionFinishedGoodReceiptService.*`
- `ProductionPostingLink`
- `ProductionOutputMaterial`
- PRTN/PADJ/FG entities and EF configurations
- transaction delete/archive policy
- current Production transaction tests
- SQL/Stock Ledger invariants

The implementation is approved only if the complete safeguards in this plan are followed.

A simple:

```text
REVERSED → NEW
```

status replacement is **NOT approved**.

---

# 1. Business goal

Make the selected Production transactions use one simple business lifecycle, matching the Inventory user concept:

```text
NEW
  ↓ POST
POSTED
  ↓ ROLLBACK
NEW
  ↓ EDIT / POST
POSTED
```

Apply to:

1. Issue to Production (`IP`)
2. Daily Production
3. Production Stock Return (`PRTN`)
4. Production Stock Adjustment (`PADJ`)
5. Finished Good Receipt (`FG`)

The user sees only:

```text
NEW
POSTED
```

for these transactions.

The ledger still retains immutable PRIMARY and REVERSAL history.

---

# 2. Core accounting rule

The business document is reusable.

The accounting/costing history is not.

Correct architecture:

```text
Business document
────────────────────────
NEW
POSTED
NEW
POSTED
NEW
...

Ledger
────────────────────────
PRIMARY  revision 1
REVERSAL revision 1

PRIMARY  revision 2
REVERSAL revision 2

PRIMARY  revision 3
...
```

Rollback does not erase the old posting.

Rollback creates the exact inverse posting and then opens the same document for its next generation.

---

# 3. Final `DocumentRevision` definition

This final review simplifies and standardizes the revision model.

> `DocumentRevision` = posting generation of the current business document.

Rules:

```text
new document        → revision 1
edit NEW draft      → same revision
POST                → same revision
ROLLBACK             → REVERSAL same revision
successful rollback → prepare next revision + 1
```

Example:

```text
rev 1 NEW
edit
edit
POST        → PRIMARY rev 1

ROLLBACK    → REVERSAL rev 1
prepare rev 2 NEW

edit rev 2
POST        → PRIMARY rev 2

ROLLBACK    → REVERSAL rev 2
prepare rev 3 NEW
```

## Important

For PRTN/PADJ/FG/Daily Production:

**Do not increment `DocumentRevision` merely because the user edits a NEW draft.**

The current repo increments PRTN/PADJ/FG revisions during normal edit. Remove that behavior as part of this standardization.

A revision must describe an economic posting generation, not every Save click.

---

# 4. IP is the controlled exception

IP uses the shared Inventory engine.

`IvInventoryPostingService.RollBackInventoryMICoreAsync()` already performs:

```text
POSTED → NEW
```

and calls:

```text
AdvanceBatchDocumentRevision(details)
```

during rollback.

Do not rewrite this generic Inventory behavior.

For IP:

```text
current Inventory detail max DocumentRevision
```

is the authoritative operational generation.

The Production IP posting link/map must synchronize to it.

Do not advance the IP detail revision a second time in Production code.

---

# 5. Non-negotiable quantity and costing invariants

A Production rollback is successful only if ALL relevant quantities and values are exactly restored.

## 5.1 Never use current cost to reverse old posting

Rollback uses the frozen original posting value.

Never reverse using current:

- moving average;
- FIFO layer cost;
- standard-cost revision;
- Item Master price;
- Production pool average;
- labour rate;
- machine rate;
- conversion-cost rate.

Required principle:

```text
rollback value = exact original posted value
```

---

## 5.2 Repost is a new economic event

Example:

```text
POST rev 1 at RM10
ROLLBACK rev 1 → reverse exactly RM10

current valid cost later becomes RM11

POST rev 2
→ use valid rev-2 cost authority
```

Do not force the repost to reuse the old cost.

---

## 5.3 Production pool invariant

After every Production movement set:

```text
ProductionPoolValuation.TrackedBaseQty
    == ProductionBalLot.BaseQty

ProductionPoolValuation.TrackedValue
    == ProductionBalLot.TotalCost
```

and:

```text
BaseQty >= 0
TotalCost >= 0
```

If:

```text
BaseQty == 0
```

then:

```text
Qty == 0
TotalCost == 0
AverageUnitCost == 0
```

No stranded value.

---

## 5.4 Inventory invariant

For IP/PRTN/FG preserve the existing V2 Inventory valuation authority.

Do not replace or redesign:

```text
InventoryValuationService
FIFO costing strategy
Moving Average strategy
Standard Cost strategy
IvInventoryHistoryWriter
StockValuationFact
```

---

# 6. V2 fail-closed rule

All five scoped Production transactions must follow:

```text
no ACTIVE compatible V2 Stock Ledger
    → no Production POST
    → no Production ROLLBACK
```

Required check:

```text
LedgerEnabled == true
Context != null
```

before stock/value mutation.

Current repo:

- PRTN: already fail-closed
- PADJ: already fail-closed
- FG: already fail-closed
- Daily POST: already fail-closed
- Daily rollback: currently conditional
- IP rollback: missing explicit fail-closed check

Fix IP and Daily rollback.

No legacy Production costing fallback.

---

# 7. StockPosting PRIMARY/REVERSAL pair guard

The current StockPosting coordinator checks that the reversal target is sealed and tenant-correct, but Production services must additionally prove that the target is the correct source posting.

Before a Production rollback begins, resolve the PRIMARY and verify:

```text
PRIMARY.SealedAtUtc != null
PRIMARY.PostingRole == PRIMARY
PRIMARY.SourceModule == expected source module
PRIMARY.SourceDocumentType == expected document type
PRIMARY.SourceDocumentId == current business document identity
PRIMARY.DocumentRevision == revision being reversed
PRIMARY.CompanyCode == current company
PRIMARY.BranchCode == current branch
```

Also require:

```text
no sealed REVERSAL already exists where
ReversesPostingId == PRIMARY.Id
```

unless replay of the same completed rollback request is being returned.

Implement this as a small reusable Production state guard, for example:

```text
ProductionPostingGenerationGuard
```

Do not put costing calculations in this guard.

---

# 8. Header posting pointer semantics

For PRTN/PADJ/FG and recommended for Daily Production:

```text
PostingId
ReversalPostingId
```

must have these semantics.

## POSTED

```text
PostingId         = current active PRIMARY
ReversalPostingId = null
```

## successful rollback to NEW

```text
PostingId         = last PRIMARY
ReversalPostingId = matching REVERSAL
Status            = NEW
DocumentRevision  = next generation
```

Do **not** clear `PostingId` after rollback.

This preserves the last completed generation audit pointer.

## next successful POST

```text
PostingId         = new PRIMARY
ReversalPostingId = null
Status            = POSTED
```

Old StockPostings remain immutable.

---

# 9. NEW-state edit guard

Because `PostingId` remains after rollback, existing guards such as:

```csharp
status == NEW && PostingId == null
```

are no longer valid.

Current PRTN and PADJ use this type of shortcut.

Replace them with the generation-state guard.

A NEW document is editable when either:

### Never posted

```text
no historical PRIMARY
no historical REVERSAL
current revision has no StockPosting
```

or:

### Rolled back historical document

```text
last PostingId exists
last ReversalPostingId exists
reversal.ReversesPostingId == PostingId
both are sealed
header current revision > last posted revision
current revision has no active PRIMARY
```

Never use `PostingId == null` as the definition of an editable NEW document.

---

# 10. ProductionPostingLink must be revision-aware

Add:

```csharp
public int DocumentRevision { get; set; }
```

to:

```text
ProductionPostingLink
```

Every scoped Production POST/ROLLBACK link must carry the actual transaction revision.

Do not use Work Order `SnapshotRevision` as document generation.

---

# 11. ProductionPostingLink is immutable after successful execution

Current code is mixed:

- IP reopens a successful POST link as DRAFT
- Daily Production changes successful POST link to REVERSED
- other transactions differ

Standardize.

A successful command link remains:

```text
SUCCEEDED
```

forever.

Do not turn historical POST links back into:

```text
DRAFT
REVERSED
```

Economic reversal is represented by:

```text
REVERSAL StockPosting
ReversesPostingId
rollback link
rollback link.OriginalPostingLinkId
reversal movements/facts
```

A new generation gets a new POST link where the transaction model uses pre-created links.

---

# 12. Revision-scoped ProductionPostingLink identity

Change link lookup/index design from:

```text
batch/document + command type
```

to:

```text
company
branch
document identity
command type
DocumentRevision
```

## IP

Unique POST link:

```text
Company
Branch
MATERIAL_ISSUE_POST
InventoryBatchNo
DocumentRevision
```

## PRTN/PADJ/FG

POST/ROLLBACK links must be revision-aware.

## Daily Production

Unique OUTPUT_POST:

```text
Company
Branch
CommandType
ProductionDocumentLineId
DocumentRevision
```

Keep request idempotency:

```text
Company + Branch + CommandType + PostingRequestId
```

unique.

---

# 13. Every original-link lookup must specify revision

Current repo contains queries such as:

```text
ProductionPostingLinks.Single(...)
where InventoryBatchNo == batchNo
and CommandType == POST
```

Those queries will fail or bind the wrong generation after the second posting.

Every rollback must resolve:

```text
POST link for this document AND this DocumentRevision
```

Never use batch number alone.

Mandatory regression:

```text
POST rev1
ROLLBACK rev1
POST rev2
ROLLBACK rev2
```

and prove rev2 rollback points to rev2 POST link, never rev1.

---

# 14. Current-generation sidecar rule

After this change, historical sidecar rows remain in database for PRTN/PADJ/FG/Daily.

Therefore every normal service path must explicitly distinguish:

```text
CURRENT rows
HISTORICAL rows
```

Normal UI/post/edit uses only:

```text
row.DocumentRevision == header.DocumentRevision
```

Historical rows are for audit/cost trace only.

---

# 15. PRTN line revision

Add:

```csharp
ProductionStockReturnLine.DocumentRevision
```

Change unique index:

```text
BatchId + ProductionBalLotId
```

to:

```text
BatchId + DocumentRevision + ProductionBalLotId
```

Require:

```text
line.DocumentRevision == line.Detail.DocumentRevision
```

for current rows.

---

# 16. PADJ line revision

Add:

```csharp
ProductionStockAdjustmentLine.DocumentRevision
```

Change uniqueness to:

```text
BatchId + DocumentRevision + ProductionBalLotId
```

Require line/detail revision equality.

---

# 17. FG source revision

Add:

```csharp
ProductionFinishedGoodSource.DocumentRevision
```

Every normal source read uses:

```text
source.DocumentRevision == receipt.DocumentRevision
```

Historical source rows must never be deleted because:

```text
ProductionFinishedGoodFact.SourceId
```

has restrictive historical lineage.

---

# 18. Daily Production material revision

Add:

```csharp
ProductionOutputMaterial.DocumentRevision
```

Current indexes are not compatible with multiple generations.

Current repo has uniqueness like:

```text
ProductionOutputId + WorkOrderMaterialId
ProductionOutputId for handoff
```

Change to:

```text
ProductionOutputId + DocumentRevision + WorkOrderMaterialId
```

and for handoff:

```text
ProductionOutputId + DocumentRevision
```

with existing filters.

Update all related indexes as needed.

---

# 19. Daily material upsert logic must be revision-scoped

Current:

```text
PersistMaterialFactsAsync()
```

loads:

```text
all ProductionOutputMaterials for output UID
```

and updates/deletes them in place.

That cannot remain.

Change it to load only:

```text
ProductionOutputId == output.Uid
AND DocumentRevision == output.DocumentRevision
```

Only current unposted rows may be updated or deleted.

Historical material rows are immutable.

Current orphan removal must only remove current-revision rows.

---

# 20. Draft edits do not advance revision

For PRTN/PADJ/FG/Daily:

When status is `NEW`, edit/save should:

1. load current revision rows;
2. ensure current revision has no PRIMARY posting;
3. remove/replace only current unposted rows;
4. keep `header.DocumentRevision` unchanged;
5. save new current rows using the same revision.

Remove current PRTN/PADJ/FG behavior that increments revision on every edit.

Revision increments only after a successfully reversed posted generation.

---

# 21. Preparing the next draft after rollback

For PRTN/PADJ/FG/Daily, successful rollback of revision N must prepare revision N+1 in the **same SQL transaction**.

Order:

```text
validate reversal
reverse qty/value
append reversal facts
run invariants
seal/prepare reversal state
clone business input into revision N+1 draft rows
set header DocumentRevision = N+1
set status NEW
save audit metadata
complete StockPosting
commit
```

If cloning/preparation fails:

```text
entire rollback fails
status remains POSTED
no physical reversal is committed
```

---

# 22. What to clone into the next draft

Clone only business input needed for user correction/direct repost.

Do not treat copied derived cost as new posting authority.

## PRTN

Copy:

- Production source identity
- requested quantity
- UOM
- reason
- remarks
- destination warehouse/location/lot input
- expiry input where applicable

Regenerate/revalidate derived cost/value at next POST.

Do not rely on copied old UnitPrice as rev N+1 authority.

---

## PADJ

Copy:

- source pool
- adjustment qty
- UOM
- reason/remarks
- approved manual/zero-cost business input where current business rule permits it

Next POST still revalidates all cost authority.

Rollback uses original old fact only.

---

## FG

Copy:

- Production source
- requested quantity
- destination warehouse/location/lot/expiry
- UOM input

Next POST recalculates current valid Production transfer value.

---

## Daily Production

Copy:

- Good/Scrap/Reject/Hold
- shift/machine/operator
- output lot
- material ConsumeQty
- variance reason
- handoff material snapshot/business input

Next POST fully revalidates current WIP/material availability and current valid costing authority.

---

# 23. Current loaders

Do not continue using unfiltered:

```csharp
Include(x => x.Lines)
Include(x => x.Sources)
```

for normal operations once historical rows are stored.

Implement clear current loaders.

Examples:

```text
LoadHeaderAsync(...)
LoadCurrentAsync(...)
LoadHistoricalGenerationAsync(...)
```

Normal:

```text
Get
Map
Save
Post
List summary
Readiness
```

must use current revision only.

---

# 24. Current list-summary queries

The current repo list queries aggregate by BatchId only.

After multiple generations this would double-count quantities.

Fix PRTN/PADJ/FG summaries to join:

```text
line/source.DocumentRevision == header.DocumentRevision
```

Daily materials/workspace also use only current revision.

IP list must return one business row per Inventory batch, not one row per historical POST link.

---

# 25. Current cost display after rollback

This was a critical final-review finding.

Because the header retains old `PostingId` but current draft rows have new IDs, code must not query old posted facts using new current line IDs.

Current examples needing adjustment:

### PRTN

Current mapper does roughly:

```text
if PostingId exists
    find fact by current ReturnLineId + old PostingId
```

### PADJ

Similar fact lookup by current AdjustmentLineId.

### FG

Similar fact lookup by current SourceId.

These become incorrect/null after rollback→new generation.

---

# 26. Cost display rule

## POSTED document

Show exact frozen posted cost from:

```text
current PostingId
current posted line/source identity
```

## NEW never-posted or NEW after rollback

Do not display old PostingId fact as if it belongs to current draft.

Use one of:

```text
current preview value
```

from valid current source/pool valuation, or:

```text
null / "Recalculated on Post"
```

according to existing UI behavior.

Do not label a preview as posted actual cost.

---

# 27. PRTN exact rollback — protected logic

Preserve current PRTN logic that validates and reverses:

- exact original ISSUE lineage;
- exact Production source contribution;
- exact base qty;
- exact Production TotalValue;
- exact Inventory destination identity;
- exact Inventory history;
- exact Inventory valuation;
- operational price snapshot;
- later Inventory movement;
- later Production movement;
- active pooled-value dependencies;
- final pool tracked qty/value.

Do not change PRTN costing formula as part of lifecycle work.

---

# 28. PADJ exact rollback — protected logic

Preserve:

```text
BuildForwardLegsAsync
BuildReversalLegsAsync
RecordForwardFactsAsync
RecordReversalFactsAsync
AssertPoolInvariantsAsync
ProductionPoolValuationService
```

PADJ remains Production-only.

Mandatory invariant:

```text
PADJ IvTrxHistory count == 0
PADJ StockValuationFact count == 0
```

Do not accidentally create Inventory movement while standardizing lifecycle.

---

# 29. FG exact rollback — protected logic

Preserve current cross-ledger transfer:

```text
Production WIP OUT exact value
→ Inventory FG IN exact value
```

Rollback:

```text
Inventory FG OUT exact old value
→ Production WIP IN exact old value
```

Preserve:

- original value;
- lot origin;
- destination balance;
- destination price snapshot;
- Inventory valuation;
- Production contribution lineage;
- dependency guards.

---

# 30. Daily Production exact rollback — protected logic

Current rollback already performs important exact operations.

Preserve:

### Material/WIP CONSUME reversal

```text
restore original Qty
restore original BaseQty
restore original TotalCost
restore original UnitCost
```

### Handoff CONSUME reversal

same exact original quantity/value.

### PRODUCE reversal

remove exact original:

```text
Qty
BaseQty
TotalCost
```

and reject negative results.

### Work Order/operation

restore:

- Good
- Scrap
- Reject
- Hold
- Remaining

using existing calculation rules.

### Production projection

rebuild exact material execution totals.

---

# 31. Daily conversion cost reversal

Preserve exact original:

```text
ProductionConversionCostFact
```

For each reversal fact:

```text
ReversesFactId       = original.Id
CostType             = original.CostType
BasisQty             = original.BasisQty
BasisUom             = original.BasisUom
RatePerOutputUnit    = original.RatePerOutputUnit
CostAmount           = original.CostAmount
```

No current rate recalculation.

---

# 32. Daily Production independent revision

Add:

```csharp
ProductionOutput.DocumentRevision = 1
```

Change StockPosting command from:

```csharp
DocumentRevision = output.SnapshotRevision
```

to:

```csharp
DocumentRevision = output.DocumentRevision
```

Work Order:

```text
SnapshotRevision
```

remains manufacturing-definition validation only.

Never increment Work Order snapshot revision merely to repost Daily Production.

---

# 33. Daily Production posting pointers

Recommended and approved:

Add:

```csharp
long? PostingId
long? ReversalPostingId
```

to `ProductionOutput`.

Use restrictive FKs to StockPosting.

This removes ambiguous discovery of the current PRIMARY by scanning movements.

Movements remain independent audit evidence.

---

# 34. Daily Production create idempotency must be independent

Current:

```text
ProductionOutput.PostingRequestId
```

is actually used as CreateAsync idempotency key and first POST-link request id.

Separate these concepts.

Because no migration compatibility is needed, preferred clean design:

```text
ProductionOutput.CreateRequestId
ProductionOutput.CreateRequestFingerprint
```

The fingerprint is immutable.

At `CreateAsync`:

1. canonicalize the creation semantic request including initial material inputs;
2. store SHA-256 creation fingerprint;
3. store CreateRequestId;
4. on same CreateRequestId replay compare incoming fingerprint to the immutable stored fingerprint;
5. return the existing document even if the document has since been edited/posted/rolled back, provided the incoming creation request matches the original creation request.

Do not compare a create retry against current mutable revision rows.

---

# 35. Daily Production POST request identity

Every Daily Production posting generation uses:

```text
current revision OUTPUT_POST ProductionPostingLink.PostingRequestId
```

not CreateRequestId.

After successful rollback prepare:

```text
new OUTPUT_POST DRAFT link
DocumentRevision = next revision
PostingRequestId = new GUID
```

---

# 36. Daily Production StockPosting fingerprint

`BuildOutputPostingCommand()` must use the posting-link RequestId and include current generation evidence.

At minimum source snapshot/fingerprint must include:

- output UID
- document no
- DocumentRevision
- Work Order ID
- operation ID
- route step ID
- SnapshotRevision + SnapshotHash
- Good/Scrap/Reject/Hold
- ProductionDate
- Output item/UOM/lot
- WIP consumption policy
- current revision material facts:
  - material/handoff identity
  - ConsumeQty
  - StandardQty
  - Base quantities
  - variance reason
  - UOM/factor
- reversal target when reversal

Do not fingerprint historical material rows.

---

# 37. Daily successful POST link

On POST:

```text
current DRAFT link
→ PENDING
→ SUCCEEDED
```

Leave it `SUCCEEDED`.

Current repo rollback sets old POST link to:

```text
REVERSED
```

Remove that behavior.

---

# 38. Daily rollback finalization

After all exact reversal checks succeed:

```text
last PostingId = original PRIMARY
ReversalPostingId = new REVERSAL
status = NEW
next DocumentRevision = N + 1
next current material rows created
new DRAFT OUTPUT_POST link created
rollback metadata saved
```

Old POST link stays `SUCCEEDED`.

Rollback link becomes `SUCCEEDED`.

Then complete/seal StockPosting and commit.

---

# 39. Daily status fields

Remove business `REVERSED` status.

Change status constraint to the scoped business states:

```text
NEW
POSTED
```

Do not globally remove any shared `REVERSED` constant used by unrelated modules.

Use repo-wide exact-text search before cleanup.

---

# 40. IP — preserve Inventory engine

Do not redesign:

```text
RollBackInventoryMICoreAsync()
PostInventoryMICoreAsync()
InventoryStockPostingCommandFactory
```

IP should remain built on the same Inventory quantity/costing engine.

This is central to the maintenance objective.

---

# 41. IP V2 fail closed

Immediately after:

```text
BeginPostingInTransactionAsync(...)
```

require:

```text
LedgerEnabled
Context != null
```

before physical rollback.

No legacy IP rollback.

---

# 42. IP revision/link behavior

Current IP rollback:

```text
reopens original POST link as DRAFT
```

Remove that.

Required:

1. rollback rev N through shared Inventory engine;
2. shared Inventory engine advances current detail revision;
3. keep old POST link `SUCCEEDED`;
4. keep old Production/Inventory movements/facts immutable;
5. read resulting current max detail revision N+1;
6. create new `MATERIAL_ISSUE_POST` DRAFT link at N+1;
7. synchronize the current operational `ProductionMaterialIssueLine` maps to the new DRAFT link and current detail revision;
8. leave Inventory batch `NEW`.

---

# 43. IP allocation maps are operational, not the immutable accounting history

`ProductionMaterialIssueLine` already has:

```text
DocumentRevision
InventoryBatchDetailId
PostingLinkId
```

but `InventoryBatchDetailId` is unique.

The shared Inventory rollback reuses/advances the operational detail rows.

Therefore do not attempt to clone two simultaneous IP map rows for the same detail ID.

After rollback:

- move/rebuild the current allocation maps to the new DRAFT link;
- update their DocumentRevision to current Inventory detail revision.

Historical audit remains in:

```text
ProductionMaterialMovement
ProductionBalLotMovement
IvTrxHistory
StockPosting
old SUCCEEDED ProductionPostingLink
```

Do not alter those.

---

# 44. IP current link resolution

Replace all assumptions of one POST link per batch.

Current link resolver must be generation-aware.

For NEW IP:

```text
select DRAFT MATERIAL_ISSUE_POST
for batch
whose DocumentRevision == current max detail revision
```

For POSTED IP:

```text
select SUCCEEDED POST link matching active/current StockPosting revision
```

Historical links are not returned as current.

---

# 45. IP list/read deduplication

Current IP Search joins all POST links.

After multiple generations that would duplicate one batch.

Change business list to one row per Inventory batch.

Line count/current draft info must use current operational mapping only.

Historical links remain accessible for audit, not the ordinary transaction list.

---

# 46. PRTN/PADJ/FG rollback-to-next-generation

After exact reversal succeeds, create next current rows with:

```text
DocumentRevision = N + 1
```

and keep old rows.

Do not delete:

```text
PRTN line referenced by ProductionStockReturnFact
PADJ line referenced by ProductionStockAdjustmentFact
FG source referenced by ProductionFinishedGoodFact
```

Restrictive FKs must stay restrictive.

---

# 47. Next generation Inventory details

For PRTN/PADJ/FG, create new `IvTrxBatchDetail` rows for revision N+1.

Do not mutate the old posted generation detail rows.

This differs from IP because PRTN/PADJ/FG own their own Production sidecar generation and are not using the generic Inventory rollback detail advancement.

---

# 48. Remove correction workflow only after backend lifecycle passes

After all backend tests pass:

remove/deprecate:

```text
CreateCorrectionAsync
CanCorrection
Create Correction buttons
REVERSED transaction filter
correction-only messages
```

for PRTN/PADJ/FG.

`CorrectedBatchId` may be removed later after a full repo reference search because no production-data migration is needed.

Do not combine cleanup with the first costing-sensitive patch.

---

# 49. Batch audit metadata

Follow Inventory behavior.

On POST:

```text
BatchStatus = POSTED
PostedDate = now
PostedBy = user
PostedCount++
PostingOperationId = request
```

On rollback:

```text
BatchStatus = NEW
RollbackDate = now
RollbackBy = user
RollbackCount++
RollbackOperationId = request
```

Keep previous PostedDate/PostedBy as last historical post, matching Inventory behavior.

Next POST updates them.

---

# 50. Next POST clears last reversal pointer

On successful PRTN/PADJ/FG/Daily POST:

```text
PostingId = new PRIMARY
ReversalPostingId = null
ReversalReason = null
```

Do not delete old reversal rows.

---

# 51. Delete/archive policy

A NEW transaction may have historical execution.

Therefore:

```text
status == NEW
```

does not mean hard-delete is safe.

Correct rule:

```text
NEW + no historical execution
    → hard delete

NEW + historical PRIMARY/REVERSAL/facts/movements
    → archive

POSTED
    → rollback first
```

---

# 52. PRTN/PADJ retention

Current PRTN/PADJ delete logic uses pointer/status shortcuts.

Replace with central evidence-based retention behavior.

Preferred:

```text
TransactionDeleteOwnerTypes.ProductionStockReturn
TransactionDeleteOwnerTypes.ProductionStockAdjustment
```

Use:

- StockPostings
- facts
- movements
- batch PostedCount/RollbackCount

as historical evidence.

---

# 53. IP/Daily current DRAFT link on archive

If a historical rolled-back NEW document is archived:

```text
current unposted DRAFT link → CANCELLED
```

Do not delete historical SUCCEEDED links.

Do not leave an executable DRAFT behind for an archived document.

---

# 54. Stale request replay

Mandatory case:

```text
POST rev1 using request A
ROLLBACK
POST rev2 using request B
retry old request A
```

The old replay must not:

- change status;
- replace current PostingId;
- change current revision;
- mutate quantity;
- mutate value;
- present old rev1 operation as current rev2.

A historical replay may report that request A completed historically, but the returned/current document state must remain rev2.

---

# 55. PRTN/PADJ/FG replay DTO rule

Current replay handlers sometimes map the current document and then overwrite returned posting IDs with the old replay operation ID.

After multi-generation support, do not overwrite current-state pointers in a way that makes an old replay look active.

Return either:

1. current document state + separate replay operation identity, or
2. a clearly historical operation result.

Do not corrupt semantic meaning of current `PostingId`.

---

# 56. Atomicity

Every scoped POST/rollback remains one SQL transaction for financial/stock state.

Keep existing:

- branch lock;
- Work Order execution lock;
- pool lock;
- Inventory balance lock;
- deterministic lock ordering;
- period-close checks;
- chronology checks.

On any failure:

```text
rollback SQL transaction
```

No partial:

- balance change;
- pool change;
- valuation fact;
- reversal fact;
- revision clone;
- next DRAFT link;
- status NEW.

---

# 57. Mandatory common lifecycle test

For each of:

```text
IP
Daily Production
PRTN
PADJ
FG
```

test:

```text
NEW rev1
POST
assert POSTED
capture PRIMARY rev1
capture all affected qty/value

ROLLBACK
assert NEW rev2
assert REVERSAL rev1
assert reversal.ReversesPostingId == primary rev1
assert all qty/value restored exactly

POST rev2 WITHOUT EDIT
assert POSTED
assert PRIMARY rev2
assert primary rev2 != primary rev1

ROLLBACK
assert NEW rev3
assert REVERSAL rev2
assert exact restoration again
```

---

# 58. Revision test

Required StockPosting history:

```text
PRIMARY 1
REVERSAL 1

PRIMARY 2
REVERSAL 2

PRIMARY 3
```

No:

```text
PRIMARY 1
REVERSAL 2
```

No duplicate PRIMARY same revision.

---

# 59. Draft-edit revision test

For PRTN/PADJ/FG/Daily:

```text
NEW rev1
save/edit
save/edit
```

assert still:

```text
DocumentRevision == 1
```

Then:

```text
POST rev1
ROLLBACK
```

assert:

```text
DocumentRevision == 2
```

Edit rev2 several times:

```text
still revision 2
```

This locks the final revision semantics.

---

# 60. Current-generation read test

After:

```text
rev1 POST
rev1 ROLLBACK
rev2 NEW
```

ordinary Get/List/Workspace must show only rev2 rows.

Assert historical rev1 rows still exist in database.

Do this for:

- PRTN
- PADJ
- FG
- Daily materials

IP ordinary read must show current operational map only and one business row.

---

# 61. Current cost-display test after rollback

After rollback to new draft:

- PRTN current line must not display old posted value using new line ID.
- PADJ current line must not report old fact as current.
- FG current source must not report old FG fact as current.

If preview is shown, assert it is calculated from current valid source and clearly treated as preview.

---

# 62. No-V2 tests

For all five:

1. POST normally.
2. disable/make active V2 unavailable.
3. attempt rollback.
4. assert failure before mutation.

Verify:

```text
status POSTED
revision unchanged
PostingId unchanged
ReversalPostingId unchanged
qty unchanged
value unchanged
no new reversal
no next draft
```

---

# 63. Failed next-generation preparation test

Inject failure after reversal calculations but before N+1 draft creation completes.

Because it is one transaction, assert:

```text
POSTED remains
physical stock remains posted
Production value remains posted
no sealed reversal
no N+1 rows
no N+1 DRAFT link
```

This specifically protects against “qty reversed but draft creation failed”.

---

# 64. IP costing/qty test matrix

Test:

- Moving Average;
- FIFO;
- Standard cost;
- lot control;
- multi-line issue;
- multiple Inventory balances;
- downstream Daily consume blocks rollback;
- active pooled dependency blocks;
- exact Inventory restore;
- exact Production MATERIAL_IN restore/removal;
- Work Order material Issued/Returned/Consumed projection;
- direct repost without edit;
- repeated two rollback cycles;
- no double detail revision advancement;
- old SUCCEEDED links preserved;
- current link resolves correctly.

---

# 65. Daily Production costing/qty test matrix

Retain and strengthen existing:

```text
Partial_production_consumption_and_rollback_preserve_exact_material_and_wip_costs
```

Cover:

- multiple input pools;
- partial material consumption;
- process handoff WIP;
- shared WIP;
- Good/Scrap/Reject/Hold;
- operation totals;
- Work Order final totals;
- source Production pool value;
- produced WIP value;
- absorbed labour;
- absorbed machine;
- other conversion cost;
- current material revision only;
- direct repost same ProductionOutput UID.

---

# 66. Daily downstream-block tests

Rollback blocked by:

- later process execution;
- later stage execution;
- downstream WIP consumption;
- FG receipt;
- active pooled-value dependency.

On block:

```text
status remains POSTED
qty/value unchanged
```

---

# 67. PRTN exact test

Example:

```text
Production before:
100 qty / RM235

POST return 20:
Production 80 / RM188
Inventory +20 / RM47

ROLLBACK:
Production 100 / RM235
Inventory exactly pre-post
```

Also verify:

- exact IvTrxHistory reversal;
- exact StockValuationFact reversal;
- exact ProductionStockReturnFact reversal;
- later Inventory use blocks;
- later Production movement blocks;
- lot identity;
- destination price snapshot;
- current rev2 list quantity not doubled;
- rev1 line FK still valid.

---

# 68. PADJ exact test

ADJUST_IN:

```text
+qty/+value
rollback -same qty/-same value
```

ADJUST_OUT:

```text
-qty/-value
rollback +same qty/+same value
```

Cover:

- current pool cost;
- original issue cost;
- standard cost;
- manual approved;
- zero cost approved.

Assert:

```text
Inventory histories = 0
Inventory valuation facts = 0
```

for PADJ.

---

# 69. FG exact test

Example:

```text
Production = 10 / RM120

POST FG 6:
Production = 4 / RM48
Inventory gets 6 / RM72

ROLLBACK:
Production = 10 / RM120
Inventory returns exactly to pre-post
```

Then direct POST rev2.

Cover:

- lot origin;
- price snapshot;
- downstream Inventory movement block;
- Production pool dependency block;
- old SourceId fact preserved;
- new current SourceId used for rev2;
- list summary not doubled.

---

# 70. Precision tests

Use existing precision rules.

Required values:

```text
qty 0.0001
unit cost 0.333334
total 1.000002
multi-line rounding residual
full depletion to zero
```

Assert no:

```text
negative qty
negative value
zero qty + non-zero value
```

---

# 71. Concurrency

Test:

### concurrent POST same generation

one PRIMARY only.

### concurrent rollback

one reversal only.

### POST vs rollback

existing locks serialize.

### stale edit RowVersion

reject.

### rollback vs downstream transaction

one safely wins; loser revalidates and fails/retries.

---

# 72. SQL Server integration tests are mandatory

Do not rely only on in-memory/SQLite behavior.

Run SQL Server-specific tests for:

- filtered unique indexes;
- revision indexes;
- restrictive FKs;
- UPDLOCK/HOLDLOCK behavior;
- concurrent POST;
- concurrent rollback;
- current revision loaders;
- StockPosting uniqueness.

---

# 73. Schema changes

Update EF + SQL scripts for:

- `ProductionPostingLink.DocumentRevision`
- `ProductionOutput.DocumentRevision`
- Daily `PostingId/ReversalPostingId` if implemented as approved
- `ProductionOutput.CreateRequestId/CreateRequestFingerprint`
- `ProductionOutputMaterial.DocumentRevision`
- `ProductionStockReturnLine.DocumentRevision`
- `ProductionStockAdjustmentLine.DocumentRevision`
- `ProductionFinishedGoodSource.DocumentRevision`
- revision-scoped unique indexes
- Daily status constraint
- related preflight scripts

Because no data migration compatibility is required:

- create one clean target schema;
- no old REVERSED compatibility branch;
- no nullable legacy revision workaround.

---

# 74. Protected core code

Do not redesign unless a pre-existing defect is independently proven:

```text
InventoryValuationService
FIFO strategy
Moving Average strategy
Standard Cost strategy
IvInventoryPostingService stock quantity math
InventoryStockPostingCommandFactory costing semantics
ProductionStockWriter math
ProductionPoolValuationService math
FinishedGoodReceiptMath
Production contribution/dependency math
absorbed conversion formulas
lot ownership rules
period close
future-stock chronology
Work Order operation sequence
```

---

# 75. Repo-wide cleanup search before removal

Before deleting REVERSED/correction code, run exact repo searches such as:

```bash
rg -n '"REVERSED"|ProductionOutputStatuses\.Reversed|CreateCorrectionAsync|CanCorrection|CorrectedBatchId'
rg -n 'PostingId is null|PostingId != null|PostingId is not null'
rg -n 'Include\(x => x\.Lines\)|Include\(x => x\.Sources\)|ProductionOutputMaterials'
rg -n 'ProductionPostingLinks.*InventoryBatchNo|LockIssueLinkByBatch|LockOutputPostLink'
```

Review every hit.

Do not perform global blind replacement.

---

# 76. Implementation sequence

## Phase 0 — baseline

Run:

```bash
dotnet build
dotnet test
```

Record pre-existing failures.

---

## Phase 1 — tests before production code

Add failing tests for:

- revision pair identity;
- rollback→NEW;
- direct repost;
- current-only read;
- cost display after rollback;
- V2 fail closed;
- delete/archive;
- stale replay;
- failed next-draft atomicity.

---

## Phase 2 — schema/revision infrastructure

Implement:

1. ProductionPostingLink revision
2. PRTN line revision
3. PADJ line revision
4. FG source revision
5. Daily Output revision
6. Daily material revision
7. Daily create-id/fingerprint separation
8. current-generation loaders
9. generation-state guard

Run schema tests.

---

## Phase 3 — IP

1. V2 fail closed.
2. revision-aware links.
3. retain shared Inventory rollback core.
4. no reopening historical link.
5. synchronize current maps after Inventory revision advance.
6. fix current link/read/list resolution.
7. direct repost and two-cycle tests.

Stop here if IP is not fully green.

---

## Phase 4 — Daily Production

1. independent DocumentRevision.
2. current material revision indexes/upsert.
3. immutable creation idempotency.
4. revision-scoped POST link.
5. posting pointer identity.
6. richer current-generation fingerprint.
7. unconditional V2 rollback.
8. exact current rollback math unchanged.
9. successful rollback prepares next revision.
10. old POST link immutable.
11. current-only workspace.
12. costing + WIP tests.

Highest-risk phase.

---

## Phase 5 — PRTN

1. current lines.
2. edit guard.
3. exact rollback unchanged.
4. next draft clone.
5. status NEW.
6. mapper cost logic.
7. retention.
8. direct repost.

---

## Phase 6 — PADJ

Same pattern, retaining all cost authority rules and zero Inventory effect.

---

## Phase 7 — FG

1. remove current unconditional revision increment.
2. make PRIMARY/REVERSAL same revision.
3. current sources.
4. exact rollback unchanged.
5. next draft clone.
6. mapper/cost trace generation awareness.
7. direct repost.

---

## Phase 8 — UI cleanup

Only after backend passes:

- NEW/POSTED only;
- remove correction action;
- simple permission buttons;
- updated rollback success text;
- current cost preview semantics.

---

## Phase 9 — final verification

Run:

```bash
dotnet build
dotnet test
```

plus all SQL Server Production/Stock Ledger suites.

Do not disable failing costing/quantity tests.

---

# 77. Stop conditions for AI coding agent

STOP and report if any implementation path requires:

1. deleting a historical fact;
2. deleting a historical line/source referenced by a fact;
3. changing old StockPosting;
4. changing old V2 history;
5. reusing old successful POST link as a new draft;
6. reusing same PRIMARY source revision;
7. reversing a PRIMARY from the wrong revision;
8. proceeding without V2;
9. current and historical rows being mixed;
10. using old PostingId fact against a new current line;
11. rollback causing negative qty/value;
12. zero qty retaining value;
13. tracked Production value != live pool value;
14. bypassing dependency guard;
15. partial rollback committing without next draft;
16. changing generic Inventory costing logic merely to support Production UI lifecycle.

No ignore/catch-and-continue workaround.

---

# 78. Definition of Done

## Lifecycle

- [ ] IP rollback → NEW
- [ ] Daily rollback → NEW
- [ ] PRTN rollback → NEW
- [ ] PADJ rollback → NEW
- [ ] FG rollback → NEW
- [ ] direct repost works
- [ ] edit after rollback works
- [ ] no scoped business REVERSED status

## Revision

- [ ] PRTN/PADJ/FG/Daily draft edits do not increment revision
- [ ] POST does not increment revision
- [ ] PRIMARY and REVERSAL same revision
- [ ] successful rollback prepares N+1
- [ ] IP syncs to Inventory detail revision
- [ ] no duplicate PRIMARY generation

## Quantity

- [ ] Inventory exact
- [ ] Production exact
- [ ] WIP exact
- [ ] material projection exact
- [ ] operation outcome exact
- [ ] Work Order outcome exact
- [ ] no negative qty

## Costing

- [ ] exact original frozen value reversed
- [ ] no current-cost rollback
- [ ] Inventory valuation exact
- [ ] Production valuation exact
- [ ] conversion cost exact
- [ ] PADJ Inventory-neutral
- [ ] precision preserved
- [ ] no stranded value

## Audit/history

- [ ] old StockPosting immutable
- [ ] old successful posting links immutable
- [ ] old movements immutable
- [ ] old facts immutable
- [ ] old histories immutable
- [ ] old PRTN/PADJ/FG sidecar rows preserved

## Read model

- [ ] current generation only
- [ ] list summary current only
- [ ] cost display status/revision-aware
- [ ] IP one row per business batch
- [ ] Daily create replay uses immutable creation fingerprint

## Safety

- [ ] V2 required everywhere
- [ ] source/revision PRIMARY guard
- [ ] downstream guards preserved
- [ ] rollback atomic
- [ ] retention correct
- [ ] stale replay safe
- [ ] concurrency safe
- [ ] SQL Server tests green
- [ ] full test suite green

---

# 79. Final architecture

## User

```text
NEW → POSTED → NEW → POSTED
```

## Stock/cost ledger

```text
PRIMARY 1
REVERSAL 1

PRIMARY 2
REVERSAL 2
...
```

## Cost rule

```text
rollback = exact inverse of old frozen value
repost   = current valid new valuation
```

## Quantity rule

```text
document becomes NEW only after every affected balance/pool/projection
has been proven exactly reversed
```

---

# 80. Final approval statement

**APPROVED FOR IMPLEMENTATION — 10/10**

This final plan is repo-aligned and implementation-ready.

The critical principle is:

> Standardize the Production document lifecycle around the proven costing/quantity engines. Do not simplify or replace those engines.

The AI coding agent must preserve exact quantity, exact monetary value, immutable V2 history, dependency protection, and atomicity before returning any Production document from POSTED to NEW.
