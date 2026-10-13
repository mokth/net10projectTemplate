# ProductionV2 — Future-Dated Inventory NEW Drafts / Post-on-Effective-Date
## Deep-Reviewed AI Coding Agent Implementation Plan — FINAL APPROVED

**Repository:** `mokth/net10projectTemplate`  
**Branch reviewed:** `productionv2`  
**Reviewed HEAD:** `d9a67ced8ea414463b80160a6e5e68e8cfdcc8fe`  
**Reviewed tree:** `65f5277f7243c21e3eee0e093b80046195120ef4`  
**Review date:** 2026-10-13  
**Status:** **APPROVED FOR IMPLEMENTATION — DEEP REVIEWED**  
**Scope:** Normal Inventory transaction lifecycle only  
**Database migration required:** **NO**  
**Core quantity algorithm change:** **NO**  
**Core costing/valuation algorithm change:** **NO**  
**Rollback algorithm change:** **NO**  
**Inventory period-close algorithm change:** **NO**

---

# 1. Executive Decision

Implement this exact business behavior:

```text
Company business date = 13-Oct-2026

Create inventory transaction dated 14-Oct-2026   ✅ ALLOW
Save as NEW                                      ✅ ALLOW
Edit while NEW                                   ✅ ALLOW
Delete/Cancel NEW                                ✅ existing rules
POST on 13-Oct                                   ❌ BLOCK

Company business date = 14-Oct-2026

POST same NEW transaction                        ✅ ALLOW
                                                   only after ALL existing
                                                   stock / chronology /
                                                   quantity / period /
                                                   source / lot / costing
                                                   validations pass
```

A future-dated document is **preparation only**.

It must not affect physical stock or financial stock until POST succeeds on or after its transaction date.

The implementation must preserve this invariant:

```text
NEW future draft
    != stock movement
    != inventory history
    != inventory valuation
    != FIFO layer
    != PO received/returned posted quantity
```

---

# 2. Deep Review Verdict

After reviewing the actual `productionv2` implementation, the feature is safe **if and only if** it is implemented with the safeguards in this plan.

The current architecture already provides the correct separation:

```text
SaveNew / Update
    |
    +-- IvTrxBatch
    +-- IvTrxBatchDetail
    |
    +-- NO IvBalLoc quantity mutation
    +-- NO IvTrxHistory creation
    +-- NO StockPosting
    +-- NO StockValuationFact
    +-- NO StockFifoLayer
    +-- NO posted PO quantity update

POST
    |
    +-- branch transaction lock
    +-- V2 posting
    +-- authoritative batch/stock locks
    +-- chronology checks
    +-- quantity checks
    +-- balance mutation
    +-- history
    +-- valuation
    +-- seal/commit
```

Therefore allowing a future date in a document whose status remains `NEW` does **not** inherently change inventory quantity or costing.

The dangerous operation is only **POST**.

This plan therefore moves the "not after business date" rule from **draft eligibility** to **posting eligibility**, while preserving every existing posting invariant.

---

# 3. Corrections Made After Reviewing the Previous Plan

The first plan was directionally correct but this deep repository review found several places where it should be tightened.

## 3.1 Do NOT globally weaken `ResolveMovementDate`

Current:

```text
IvStockMovementRules.ResolveMovementDate(...)
```

performs:

1. default/normalize document date;
2. reject a document date after company business date.

Do not change its meaning globally.

Other callers may rely on the strict behavior.

Instead add an explicit draft-only normalizer.

---

## 3.2 The authoritative POST gate must run after the batch is locked

A UI check or dispatcher-only check is not enough.

Example race:

```text
POST preflight reads date = 13-Oct
another session edits NEW document -> 14-Oct
posting continues
```

The final check must use the **authoritative locked `IvTrxBatch` row** immediately before normal posting processing.

This prevents a future-dated post from entering because of concurrent editing.

---

## 3.3 Shared Inventory posting cores are used by Production

Repository review confirmed shared calls such as:

```text
Production Material Issue
    -> PostStockOutInTransactionAsync(...)
    -> Inventory stock-out core

Production Stock Return
    -> PostStockInInTransactionAsync(...)
    -> Inventory stock-in core

Stock Count
    -> PostStockAdjustmentInTransactionAsync(...)
    -> Inventory ADJ core
```

Therefore this feature must not blindly redefine every shared transaction type.

The new future-**draft** behavior in this task applies only to the normal Inventory transaction codes:

```text
MR
CR
GR
NG
MI
SC
VR
TR
ADJ
```

Do not change the lifecycle policy of:

```text
IP    Issue to Production
PRTN  Production Stock Return
PADJ  Production Stock Adjustment
FG    Finished Goods
SP    Sales stock-out
```

Production/Sales behavior is outside this task.

Stock Count remains protected by its own `ValidateCountDate()` and must continue rejecting future count dates.

---

## 3.4 Correct Stock Transfer implementation point

The current repository does **not** have a `PostInventoryTRCoreAsync` matching the previous plan wording.

The current Stock Transfer posting implementation is:

```text
IvInventoryPostingService.PostInventoryTRAsync(...)
```

and that method itself:

```text
starts transaction
takes branch stock lock
starts V2 ledger
locks batch
validates
locks stock
mutates balances
creates history
completes ledger
commits
```

The plan below uses the actual method.

---

## 3.5 Remove automatic lot-number prefix changes from this task

The earlier plan proposed changing `yyMMdd` automatic lot prefixes from creation-day to document transaction date.

That is **not required** to implement safe future drafts.

The repository does not establish that the date embedded in the auto lot number is contractually the inventory transaction date.

Changing it could change existing business numbering semantics.

Therefore:

```text
DO NOT change lot-number prefix generation in this task.
```

If desired, that should be reviewed separately.

---

## 3.6 MR / Customer Return expiry validation does need a narrow correction

This is required for consistency.

Current save-time MR/CR validation compares expiry against `_dates.Today`.

The authoritative stock-in posting core already compares expiry against:

```text
batch.TrxDtTime.Date
```

For a future draft:

```text
Today       = 13-Oct
TrxDate     = 14-Oct
ExpiryDate  = 13-Oct
```

today-based draft validation could accept it, but posting correctly rejects it.

Fix only this inconsistency:

```text
MR/CR expiry date must be >= document transaction date.
```

This is not a costing/quantity change.

It aligns draft validation with the posting core that already exists.

---

# 4. Repository Facts Supporting Approval

## 4.1 Current future-date rule

`ErpWeb.Core/Inventory/IvStockDateRules.cs`

contains:

```csharp
IsFutureMovementDate(documentDate, businessDate)
```

which treats:

```text
DocumentDate > BusinessDate
```

as future.

`ErpWeb.Core/Inventory/IvStockMovementRules.cs`

uses this from:

```text
EnsureValidMovementDate
ResolveMovementDate
```

---

## 4.2 Correct business-date authority

`ErpWeb.Core/Services/CurrentDateService.cs`

provides:

```text
ICurrentDateService.Today
ICurrentDateService.Now
```

using the configured company timezone, with `Asia/Kuala_Lumpur` fallback.

The new POST gate must use:

```csharp
_currentDate.Today
```

or equivalent injected `ICurrentDateService`.

Do not use:

```text
DateTime.Today
DateTime.UtcNow.Date
StockPosting.BusinessDate
```

as the authority.

Important:

```text
StockPosting.BusinessDate
```

is currently populated from:

```csharp
command.EffectiveAt.Date
```

so that field is the posting/effective date, not the live company "today" used by this business rule.

---

# 5. Quantity Integrity Review

The current quantity-changing repository methods are in:

```text
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs
```

Stock decrease:

```csharp
StdQty = StdQty - qty
TransDate = transDate
```

Stock increase:

```csharp
StdQty = StdQty + qty
TransDate = transDate
```

These operations occur in the POST workflow, not normal transaction SaveNew/Update.

A future `NEW` draft therefore does not change `IvBalLoc.StdQty`.

The implementation must not add any new draft-time balance write.

---

# 6. Costing Integrity Review

Stock Ledger V2 remains untouched.

Reviewed areas include:

```text
StockPostingCoordinator
InventoryStockPostingCommandFactory
InventoryValuationService
StockFifoLayer
StockValuationFact
```

The current V2 flow:

```text
POST starts
    -> StockPosting created in transaction
    -> inventory history generated
    -> InventoryValuationService values posting
    -> FIFO / Moving Average / Standard Cost logic runs
    -> posting sealed
    -> transaction commits
```

A failed post rolls its DB transaction back.

Future-draft SaveNew/Update does not call valuation.

Therefore this feature does not require changing:

```text
FIFO
Moving Average
Standard Cost
StockCostState
StockFifoLayer
StockFifoLayerConsumption
StockValuationFact
valuation formulas
valuation rounding
```

These must remain byte-for-byte logically equivalent unless a compile-only constructor change requires otherwise.

---

# 7. Existing Costing Chronology Must Remain

`InventoryValuationService` already calls:

```text
RejectBackdatedPoolsAsync(...)
```

for valuation flows.

FIFO layers remain ordered by:

```text
ReceiptEffectiveAt
then Id
```

A future draft posted when its date becomes valid still uses its document date as V2:

```text
EffectiveAt
```

The feature must not bypass the existing valuation chronology.

---

# 8. Existing Stock Chronology Must Remain

Do not change any behavior in:

```text
IvInventoryPostingService.Chronology.cs
```

Preserve:

### INV-03

```text
IvBalLoc.TransDate <= DocumentDate
```

for stock availability.

### INV-04

Reject a new posting when a later calendar-day movement already exists on the balance.

### INV-05

Rollback must respect persisted movement order, including same-day `IvTrxHistory.Id` ordering.

### Rollback TransDate repair

Continue resolving `IvBalLoc.TransDate` from remaining history after rollback.

---

# 9. Inventory Period Close Must Remain

Do not change:

```text
IvPeriodCloseGuard
IvPeriodCloseService
IvPeriodCloseHdr
```

Current rule:

```text
document/effective date <= latest CLOSED PeriodTo
    -> reject
```

The rule is keyed on the movement's own date, never today's date.

Future-draft support does not mean closed-period support.

Expected:

```text
Date inside CLOSED inventory period
Save NEW    ❌
Update NEW  ❌
POST        ❌
Rollback    ❌

Reopen period
    -> existing normal rules apply
```

---

# 10. Scope — Normal Inventory Transactions

Apply the future-NEW behavior to:

| Code | Transaction | Future NEW Save | Future Edit | Early POST |
|---|---|---:|---:|---:|
| MR | Miscellaneous Receipt | ALLOW | ALLOW | BLOCK |
| CR | Customer / Stock Return | ALLOW | ALLOW | BLOCK |
| GR | Goods Receipt | ALLOW | ALLOW | BLOCK |
| NG | Non-stock Goods Receipt | ALLOW | ALLOW | BLOCK |
| MI | Miscellaneous Issue | ALLOW | ALLOW | BLOCK |
| SC | Scrap | ALLOW | ALLOW | BLOCK |
| VR | Vendor Return | ALLOW | ALLOW | BLOCK |
| TR | Stock Transfer | ALLOW | ALLOW | BLOCK |
| ADJ | Stock Adjustment | ALLOW | ALLOW | BLOCK |

---

# 11. Explicit Out of Scope

Do not change future-date lifecycle policy for:

```text
IP
FG
PRTN
PADJ
SP
Daily Production
Work Order Transfer
production WIP movements
Sales shipment posting
```

Do not change Stock Count's date policy.

Do not create:

```text
FutureStock
ScheduledStock
ProjectedBalLoc
```

Do not add a database migration.

---

# 12. Phase 1 — Add a Draft-Only Date Normalizer

## File

```text
ErpWeb.Core/Inventory/IvStockMovementRules.cs
```

Add a clearly named helper such as:

```csharp
public static DateTime ResolveDraftMovementDate(
    DateTime requestedDate,
    DateTime businessDate)
{
    return requestedDate == default
        ? businessDate.Date
        : requestedDate.Date;
}
```

Exact name may vary.

Required semantics:

```text
default date -> company business date
past date    -> preserve date
today        -> preserve date
future date  -> preserve date
```

It must perform **no future-date rejection**.

---

# 13. Preserve the Existing Strict Helper

Do not remove or weaken:

```text
IvStockDateRules.IsFutureMovementDate
IvStockMovementRules.EnsureValidMovementDate
IvStockMovementRules.ResolveMovementDate
```

The existing strict helper remains useful for posting and other workflows.

This separation is intentional:

```text
ResolveDraftMovementDate
    -> persistence normalization only

EnsureValidMovementDate / strict ResolveMovementDate
    -> effective stock movement policy
```

---

# 14. Phase 2 — Update Normal Inventory SaveNew / Update

Replace only the future-rejecting date resolution in normal inventory draft SaveNew/Update.

Files:

```text
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvStockReturnService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs
ErpWeb.Core/Inventory/IvMiscIssueService.cs
ErpWeb.Core/Inventory/IvScrapService.cs
ErpWeb.Core/Inventory/IvVendorReturnService.cs
ErpWeb.Core/Inventory/IvStockTransferService.cs
ErpWeb.Core/Inventory/IvStockAdjustmentService.cs
```

Change conceptually from:

```csharp
var (trxDate, movementDateError) =
    IvStockMovementRules.ResolveMovementDate(
        request.TrxDate,
        _dates.Today);

if (movementDateError is not null)
    return Fail(...);
```

to:

```csharp
var trxDate =
    IvStockMovementRules.ResolveDraftMovementDate(
        request.TrxDate,
        _dates.Today);
```

Retain period-close validation.

---

# 15. Draft Save Must Remain Non-Posting

After implementation, inspect each changed SaveNew / Update method and confirm it still only persists the normal draft document.

It must not call:

```text
IncreaseBalLocQtyAsync
DecreaseBalLocQtyAsync
FindOrCreateBalLocAsync
AddHistory
StockPostingCoordinator.Begin...
InventoryValuationService
ApplyGoodsReceiptPoQtyAsync
ApplyVendorReturnPoQtyAsync
```

Draft SaveNew/Update must not mutate:

```text
IvBalLoc
IvTrxHistory
StockPosting
StockValuationFact
StockFifoLayer
PO posted receipt/return quantity
```

This is a mandatory code-review gate.

---

# 16. Phase 3 — MR / CR Expiry Alignment

## Files

```text
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvStockReturnService.cs
```

Currently their save-time line validation uses `_dates.Today` for expiry.

Change the flow so the normalized draft transaction date is resolved **before** line validation.

Pass `trxDate` into:

```text
ValidateLinesAsync
ValidateLineAsync
```

and use:

```text
ExpiryDate >= trxDate.Date
```

instead of:

```text
ExpiryDate >= _dates.Today
```

The posting core already checks expiry against `batch.TrxDtTime.Date`, so this aligns draft behavior with authoritative POST behavior.

---

# 17. MR / CR UI Validation Alignment

Update only where the UI currently compares lot expiry to `AppToday`.

Files:

```text
ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.cs
ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor.cs
```

Use:

```text
Header.TrxDate.Date
```

for the expiry validation message.

Do not make UI the authority.

The server remains authoritative.

---

# 18. Goods Receipt Already Has the Correct Expiry Semantics

Repository review confirms:

```text
IvGoodsReceiptService.ValidateLinesAsync(...)
```

already receives `trxDate`.

It rejects:

```text
expiry < receipt transaction date
```

Do not redesign this logic.

Only replace its SaveNew/Update date resolver with the draft normalizer.

Preserve:

```text
PO revision checks
PO quantity ceilings
pack-size conversion
lot validation
cost evidence resolution
```

---

# 19. Phase 4 — Add Company Business Date to Posting Service

## File

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
```

Inject:

```csharp
ICurrentDateService
```

as a required dependency.

Example:

```csharp
private readonly ICurrentDateService _currentDate;
```

Do not use an optional/null date service fallback.

The future-post rule is an integrity rule and must always have a deterministic company date.

---

# 20. DI Impact

Production DI already contains:

```text
services.AddScoped<ICurrentDateService, CurrentDateService>();
services.AddScoped<IIvInventoryPostingService, IvInventoryPostingService>();
```

Therefore runtime DI does not require a new registration.

Constructor signature changes will require compilation updates to test fixtures/manual instantiations.

---

# 21. Known Manual Constructor Call Sites to Update

The repository review found `new IvInventoryPostingService(...)` in inventory test/fixture code including:

```text
ErpWeb.Tests/Inventory/Transaction/InventoryLedgerTestFixture.cs
ErpWeb.Tests/Inventory/Transaction/IvGoodsReceiptServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingSqlServerConcurrencyTests.cs
ErpWeb.Tests/Inventory/Transaction/IvMiscIssuePostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvMiscReceiptServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvPeriodCloseGuardTests.cs
ErpWeb.Tests/Inventory/Transaction/IvScrapPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockAdjustmentPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockCountServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockCountSqlServerConcurrencyTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvVendorReturnPostingServiceTests.cs
```

Production test fixture also includes:

```text
ErpWeb.Tests/Production/Transaction/ProductionLedgerTestFixture.cs
```

Use `FixedCurrentDateService` for deterministic tests.

After implementation run a full solution compile to catch every constructor call site, including any that may have been added after this reviewed HEAD.

---

# 22. Phase 5 — Explicit Normal-Inventory Type Gate

In `IvInventoryPostingService`, add one private policy helper.

Example:

```csharp
private static bool RequiresInventoryBusinessDateGate(string? trxType)
{
    return trxType is not null
        && NormalInventoryFutureDraftTypes.Contains(trxType);
}
```

where the allowed set is exactly:

```text
MR
CR
GR
NG
MI
SC
VR
TR
ADJ
```

Use ordinal-ignore-case comparison.

Do not include:

```text
IP
FG
PRTN
PADJ
SP
```

in this feature's new lifecycle behavior.

---

# 23. Phase 6 — Authoritative Locked-Batch POST Validation

Use:

```csharp
IvStockMovementRules.EnsureValidMovementDate(
    batch.TrxDtTime,
    _currentDate.Today)
```

only during POST for this feature's normal inventory types.

The check must happen:

```text
after authoritative batch lock
before any stock/history/cost mutation
```

Preserve existing period-close priority.

Recommended order:

```text
1. Lock batch
2. Confirm expected TrxType
3. Archived/deleted guard
4. Existing IvPeriodCloseGuard
5. NEW future-post business-date guard   <-- ADD
6. Status guard
7. Details/master validation
8. stock locks
9. chronology
10. quantity validation
11. stock mutation
12. history
13. valuation
14. status POSTED
15. commit
```

Keeping period-close before the new check preserves existing closed-period error precedence.

---

# 24. Exact Posting Locations

## 24.1 MR / CR / stock GR path

Method:

```text
PostInventoryMRCoreAsync(...)
```

It is also reused for `PRTN`, so apply the new business-date check only when:

```text
expectedTrxType is MR / CR / GR
```

Do not apply this feature's type policy to `PRTN`.

---

## 24.2 NG

Method:

```text
PostNonStockGoodsReceiptCoreAsync(...)
```

Add the normal inventory business-date POST gate after period-close and before status/details/history changes.

Even though NG is non-stock, it is still an Inventory transaction document and its posting date must not become effective before its transaction date.

---

## 24.3 MI / SC / VR

Method:

```text
PostInventoryMICoreAsync(...)
```

This shared stock-out core is also used by integrated transaction types such as IP/SP.

Gate only:

```text
MI
SC
VR
```

Do not change IP/SP lifecycle policy in this task.

---

## 24.4 ADJ

Method:

```text
PostInventoryADJCoreAsync(...)
```

ADJ is in scope.

Stock Count also generates an ADJ batch, but Stock Count already rejects a future CountDate through:

```text
IvStockCountService.ValidateCountDate()
```

Therefore this additional POST guard is consistent and should not change valid Stock Count behavior.

---

## 24.5 TR

Actual current method:

```text
PostInventoryTRAsync(...)
```

After it locks `IvTrxBatch`, apply the new business-date check after period-close and before status/details/stock mutation.

Do not refer to a non-existent `PostInventoryTRCoreAsync`.

---

# 25. Do NOT Add an Early Date Gate to `BeginStandaloneLedgerAsync`

Final repository review confirms that an additional early future-date preflight inside:

```text
BeginStandaloneLedgerAsync(...)
```

is **not necessary** and should **not** be added in this task.

Reason:

- `BeginStandaloneLedgerAsync` is a shared ledger bootstrap path.
- Production also uses the same Inventory posting/ledger infrastructure.
- A failed normal Inventory POST already runs inside the caller-owned database transaction.
- The authoritative locked-batch future-date check will execute before quantity/history/cost mutation.
- If a temporary V2 posting context was created before that failure, the surrounding transaction rollback prevents persistence.

Therefore the safest implementation is:

```text
NO new future-date policy inside BeginStandaloneLedgerAsync
```

Keep that method's existing behavior unchanged.

The only authoritative business-date gate must be placed after the real `IvTrxBatch` row is locked in the normal Inventory posting path.

This reduces scope and further protects Production from accidental behavioral changes.

---

# 26. Standard Future-POST Error

Reuse the central stock movement message where practical.

Preferred user-facing result:

```text
Transaction date 14/10/2026 is after the business date 13/10/2026.
This document may remain NEW, but it cannot be posted until 14/10/2026.
```

Do not produce different wording per transaction screen.

---

# 27. Do Not Change Posting Quantity Logic

The following must remain unchanged:

```text
IvStockPostingRepository.IncreaseBalLocQtyAsync
IvStockPostingRepository.DecreaseBalLocQtyAsync
IvStockPostingRepository.FindOrCreateBalLocAsync
ValidateBalanceStockDate
ValidateNoLaterDayMovementsAsync
```

No new quantity math is needed.

---

# 28. Do Not Change Costing Logic

Do not modify:

```text
InventoryValuationService
StockPostingCoordinator valuation formulas
FIFO consumption algorithm
Moving Average algorithm
Standard Cost algorithm
StockCostState calculation
StockFifoLayer calculation
StockValuationFact calculation
ProductionPoolValuationService
```

The feature is a lifecycle/date-gate change only.

---

# 29. Do Not Reprice / Re-Cost Future Drafts as Part of This Task

A draft may already contain document price/cost evidence based on the existing transaction's rules.

Do not introduce a new "refresh costing because day changed" process.

At POST, retain the current V2 valuation authority and existing evidence semantics.

If the existing ERP later needs price/evidence refresh policy, review it separately.

---

# 30. Post-Time Revalidation Remains Mandatory

A future draft does not reserve stock.

Example:

```text
13-Oct
MI draft dated 14-Oct
requested qty = 80
current stock = 100
Save NEW succeeds

later 13-Oct
another transaction consumes 50

14-Oct
actual current stock = 50
future draft POST tries to issue 80
```

Expected:

```text
POST ❌
insufficient quantity
```

Never trust draft-time available quantity as posting authority.

---

# 31. Existing Stock-Out Revalidation Must Remain

At POST preserve:

```text
locked source BalLoc
current StdQty
source identity
warehouse
location
lot
status
UOM
stock date
later-day movement check
```

Do not add reservations as part of this feature.

---

# 32. Existing Stock-In Revalidation Must Remain

At POST preserve:

```text
active item
active warehouse/location/status
lot policy
expiry policy
existing destination stock date
later movement check
find/create lot
find/create balance
```

The stock-in core already revalidates expiry against `batch.TrxDtTime`.

---

# 33. GR / PO Integrity Must Remain

Goods Receipt posting currently performs PO snapshot/locking/quantity validation and only updates PO received quantity after the inventory posting succeeds.

Do not change that ordering.

Future early POST failure must leave:

```text
PO received quantity unchanged
GR batch NEW
no history
no valuation
```

---

# 34. VR / PO Integrity Must Remain

Vendor Return PO quantity adjustment occurs only after the stock-out posting succeeds.

Future early POST failure must leave:

```text
PO returned quantity unchanged
VR batch NEW
stock unchanged
history unchanged
valuation unchanged
```

---

# 35. No Draft-Time Stock Reservation

Do not introduce:

```text
hard reservation
soft reservation
ATP decrement
future balance
projected on-hand
```

A NEW future draft is preparation, not ownership of stock.

---

# 36. Stock Count Must Remain Unchanged

`IvStockCountService.ValidateCountDate()` currently rejects:

```text
CountDate > _currentDate.Today
```

Keep that exact policy.

Example:

```text
Today     13-Oct
CountDate 14-Oct
```

Expected:

```text
Save/Generate/Preview/Post ❌
```

Reason: a physical stock count is an as-of physical event, not a preparable stock transaction.

---

# 37. Production Regression Boundary

Because Production uses shared Inventory components, the AI agent must explicitly prove this task did not alter Production transaction behavior.

At minimum run the existing relevant production tests including the fixture/path that uses:

```text
IIvInventoryPostingService
PostStockOutInTransactionAsync
PostStockInInTransactionAsync
```

No new future-draft behavior is to be introduced for Production by this plan.

---

# 38. Sales Regression Boundary

`SP` is not in this feature's new future-draft policy.

Do not modify Sales shipment/date policy in this implementation.

If Sales needs the same "future NEW, post on date" behavior, review it separately against SO/DO/Invoice/commitment logic.

---

# 39. No Schema Change

No table/column/index change is required.

Do not add fields such as:

```text
ScheduledPostDate
FuturePosting
EffectiveStatus
ReadyToPost
```

Existing:

```text
IvTrxBatch.TrxDtTime
IvTrxBatch.BatchStatus
```

are sufficient.

---

# 40. UI Scope — Keep Minimal

The current Inventory date editors already allow users to select a future date.

Therefore no broad UI redesign is required.

Mandatory UI changes only:

```text
MR expiry validation -> compare to Header.TrxDate
CR expiry validation -> compare to Header.TrxDate
```

Everything else may rely on the server POST error.

---

# 41. Multi-Select POST — Preserve Existing Backend Semantics

`IvInventoryPostingService.DispatchAsync` currently processes batches individually.

A selection can therefore produce:

```text
some succeeded
some failed
```

and the service returns a partial-post summary.

Do not redesign batch atomicity in this task.

Optional UI enhancement:

before opening the POST confirmation, the list page may identify selected future-dated NEW rows and warn the user.

But this is optional UX and must not become the only protection.

Do not change backend all-or-nothing semantics as part of this feature.

---

# 42. Documentation Update

Update:

```text
plans/inventory_stock_integrity_hardening_plan.md
```

Current wording says roughly:

```text
INV-06 — No normal future-dated stock movement
TransactionDate > BusinessDate => reject
Before save/post ...
```

Reword the invariant to distinguish a document draft from a stock movement.

Recommended:

```text
INV-06 — No future-dated POSTED stock movement

A NEW normal Inventory document may be prepared with a future
TransactionDate.

It does not become inventory movement authority until POST.

POST requires:
    TransactionDate <= CompanyBusinessDate

All period-close, stock-date, chronology, quantity and costing
rules continue to apply.

Stock Count remains future-date prohibited.

Production/Sales lifecycle policy is unchanged by this phase.
```

Do not weaken the actual posting invariant.

---

# 43. Mandatory Unit Test — Draft Date Normalization

Extend:

```text
ErpWeb.Tests/Inventory/Transaction/IvStockChronologyRulesTests.cs
```

Preserve existing strict tests:

```text
IsFutureMovementDate_rejects_after_business_date
ResolveMovementDate_defaults_and_rejects_future
```

Add separate draft-normalizer tests:

```text
business = 13-Oct

default     -> 13-Oct
12-Oct      -> 12-Oct
13-Oct      -> 13-Oct
14-Oct      -> 14-Oct, no error
```

This proves strict posting semantics were not weakened.

---

# 44. Mandatory Service Tests — Save Future NEW

For each normal transaction family:

```text
MR
CR
GR
NG
MI
SC
VR
TR
ADJ
```

use:

```text
FixedCurrentDateService(13-Oct-2026)
TrxDate = 14-Oct-2026
```

Expected:

```text
SaveNew succeeds
BatchStatus = NEW
TrxDtTime = 14-Oct-2026
```

Where the service supports Update:

```text
edit while still 13-Oct
Save Update succeeds
BatchStatus remains NEW
TrxDtTime remains/changes to requested future date
```

---

# 45. Mandatory "Draft Mutates Nothing" Assertions

Immediately after future SaveNew/Update, assert no posting side effects.

At minimum:

```text
no new IvTrxHistory
no sealed StockPosting for the draft
no StockValuationFact
no StockFifoLayer change
IvBalLoc quantities unchanged
```

For GR/VR also assert relevant PO posted quantities unchanged.

---

# 46. Mandatory POST Too-Early Test

Create:

```text
Today   = 13-Oct
TrxDate = 14-Oct
Status  = NEW
```

POST.

Expected:

```text
Succeeded = false
future-date message returned
BatchStatus remains NEW
PostedDate remains null
PostedBy remains null
PostedCount unchanged
PostingOperationId unchanged/null
```

and:

```text
IvBalLoc unchanged
IvTrxHistory unchanged
StockPosting not persisted
StockValuationFact unchanged
StockFifoLayer unchanged
```

---

# 47. Mandatory POST-On-Date Test

Using the same persisted draft, run with:

```text
FixedCurrentDateService(14-Oct-2026)
```

POST again.

Expected:

```text
normal posting rules execute
POST succeeds if all business rules remain valid
BatchStatus = POSTED
```

Then assert transaction-specific stock/history/cost values match the current established expectations.

---

# 48. Mandatory Overnight Stock-Change Test

For a stock-out transaction:

```text
13-Oct
Future MI dated 14-Oct
draft qty = 80
current stock = 100

13-Oct later
another valid post reduces current stock to 50

14-Oct
POST draft qty 80
```

Expected:

```text
POST fails insufficient quantity
stock remains 50
no future-draft history is created
no valuation for failed posting
```

This proves draft-time stock is not treated as reserved authority.

---

# 49. Mandatory Later-Movement Chronology Test

Example:

```text
future draft effective 14-Oct
existing later movement 15-Oct exists
```

When attempting to post the 14-Oct draft:

```text
existing INV-04/backdate chronology must reject
```

Do not bypass it merely because the draft existed first.

---

# 50. Mandatory Closed-Period Regression

Existing behavior must remain:

```text
transaction date in CLOSED August

Save      ❌
Update    ❌
POST      ❌
Rollback  ❌
```

Run:

```text
IvPeriodCloseGuardTests
```

especially:

```text
Rollback_after_close_refuses_and_mutates_nothing
Rollback_succeeds_once_the_period_is_reopened
```

---

# 51. Mandatory Rollback Regression

No rollback code should be changed.

Run rollback tests verifying:

```text
latest movement first
same-day History.Id ordering
qty restoration
TransDate repair
V2 exact reversal
closed period blocker
POSTED -> NEW lifecycle
```

---

# 52. Mandatory Costing Equivalence Test

This is a high-value safety proof.

Create two semantically identical inventory transactions.

### A — prepared early

```text
13-Oct:
save NEW dated 14-Oct

14-Oct:
POST
```

### B — created normally

```text
14-Oct:
save NEW dated 14-Oct
POST
```

Compare after posting.

Ignoring IDs/audit timestamps, assert equivalent:

```text
IvBalLoc quantity
IvBalLoc stock identity
IvTrxHistory quantity
EffectiveAt
valuation base quantity
valuation amount
unit cost
cost state
FIFO layer quantity/value where applicable
```

The result must not depend on the day the NEW draft was created.

---

# 53. Cost Method Coverage

Run representative equivalence tests under the repository-supported cost methods:

```text
FIFO
Moving Average
Standard Cost
```

Do not change costing code to make these tests pass.

If one transaction type is not meaningful under one method, use the existing suitable transaction fixture.

The purpose is to prove:

```text
draft creation date has zero valuation effect
effective POST date remains costing authority
```

---

# 54. MR / CR Expiry Tests

Use:

```text
Today       = 13-Oct
TrxDate     = 14-Oct
```

Test:

```text
Expiry 13-Oct -> Save rejected
Expiry 14-Oct -> allowed
Expiry 15-Oct -> allowed
```

Then verify POST core gives the same rule.

This removes save/post inconsistency.

---

# 55. Goods Receipt Regression Tests

Confirm existing behavior remains:

```text
expiry validated against receipt transaction date
PO latest revision required
PO line ceiling enforced
lot uniqueness enforced
UOM/pack conversion preserved
cost evidence preserved
```

Future draft support must not bypass these rules.

---

# 56. Constructor / DI Compile Gate

Because `IvInventoryPostingService` receives a new required dependency:

```text
ICurrentDateService
```

the implementation is not complete until:

```text
dotnet build
```

passes for the full solution.

Do not solve failing test constructors by making the date dependency optional.

Supply `FixedCurrentDateService` explicitly.

---

# 57. Inventory Test Suites to Run

At minimum run:

```text
InventoryValuationServiceTests
IvGoodsReceiptServiceTests
IvInventoryPostingServiceTests
IvInventoryPostingSqlServerConcurrencyTests
IvMiscIssueLedgerAuthorityTests
IvMiscIssuePostingServiceTests
IvMiscReceiptServiceTests
IvPeriodCloseGuardTests
IvPeriodCloseServiceTests
IvScrapLedgerAuthorityTests
IvScrapPostingServiceTests
IvStockAdjustmentLedgerAuthorityTests
IvStockAdjustmentPostingServiceTests
IvStockChronologyRulesTests
IvStockCountLedgerAuthorityTests
IvStockCountServiceTests
IvStockCountSqlServerConcurrencyTests
IvStockReturnLedgerAuthorityTests
IvStockReturnPostingServiceTests
IvStockReturnServiceTests
IvStockTransferLedgerAuthorityTests
IvStockTransferPostingServiceTests
IvVendorReturnLedgerAuthorityTests
IvVendorReturnPostingServiceTests
```

Plus the full Inventory test category if available.

---

# 58. Production Regression Suites

Because of the shared Inventory posting service, run relevant Production suites, including:

```text
ProductionLedgerTestFixture dependent tests
ProductionStockLedgerPostingTests
ProductionStockLedgerSqlServerTests
ProductionStockMaterialExecutionTests
ProductionStockTransactionSqlServerTests
ProductionMaterialIssueSequenceOverlapTests
ProductionStockAdjustmentServiceTests
FinishedGoodReceiptTests
ProductionDailyOutputCoreTests
ProductionOutputEntryServiceTests
```

Goal:

```text
zero behavior change to Production from this feature
```

---

# 59. SQL Server Concurrency Gate

Do not rely only on SQLite/unit tests.

Run the existing SQL Server concurrency suites.

Add a focused test where practical:

```text
session A begins POST
session B attempts to edit the same NEW batch transaction date
```

Required invariant:

```text
no future-dated POST can commit
no qty/history/valuation mismatch can commit
```

The final date decision must be based on the authoritative locked batch.

---

# 60. Failure Atomicity Gate

Use existing test hooks / transaction-failure tests where available.

For any failed future POST, verify there is no committed half-state:

```text
balance moved but no history
history but NEW batch
StockPosting but no valuation
PO qty moved but no stock
```

Everything must roll back atomically.

---

# 61. Core Files That Must NOT Be Functionally Modified

Unless required only for compilation/tests, do not functionally change:

```text
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.Chronology.cs
ErpWeb.Core/Inventory/IvPeriodCloseGuard.cs
ErpWeb.Core/Inventory/IvPeriodCloseService.cs
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs
ErpWeb.Core/StockLedger/InventoryValuationService.cs
ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs
```

The objective is to route the lifecycle at the correct boundary, not modify proven stock/cost engines.

---

# 62. Expected Main Files to Change

Core policy:

```text
ErpWeb.Core/Inventory/IvStockMovementRules.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
```

Normal inventory draft services:

```text
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvStockReturnService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs
ErpWeb.Core/Inventory/IvMiscIssueService.cs
ErpWeb.Core/Inventory/IvScrapService.cs
ErpWeb.Core/Inventory/IvVendorReturnService.cs
ErpWeb.Core/Inventory/IvStockTransferService.cs
ErpWeb.Core/Inventory/IvStockAdjustmentService.cs
```

Small UI consistency only:

```text
ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.cs
ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor.cs
```

Tests/fixtures:

```text
relevant ErpWeb.Tests inventory transaction tests
constructor call sites
ProductionLedgerTestFixture where constructor signature requires it
```

Documentation:

```text
plans/inventory_stock_integrity_hardening_plan.md
```

Do not edit every Inventory list/entry page unless a verified compile/UI requirement exists.

---

# 63. Implementation Sequence

AI coding agent must implement in this order:

```text
1. Add draft-only date normalization helper.
2. Add unit tests proving strict post helper remains strict.
3. Change normal Inventory SaveNew/Update to draft date normalization.
4. Move MR/CR normalized date ahead of line validation.
5. Pass MR/CR trxDate to expiry validation.
6. Align MR/CR UI expiry validation with Header.TrxDate.
7. Inject ICurrentDateService into IvInventoryPostingService.
8. Update all manual constructor/test fixtures.
9. Add explicit normal-inventory transaction-type policy helper.
10. Add mandatory locked-batch future-date guard:
      - MR/CR/GR stock-in core
      - NG core
      - MI/SC/VR stock-out core
      - ADJ core
      - actual TR PostInventoryTRAsync
11. Do NOT add a future-date policy to BeginStandaloneLedgerAsync.
12. Do NOT touch rollback paths.
13. Do NOT touch quantity/costing algorithms.
14. Update INV-06 documentation.
15. Add future draft service tests.
16. Add future POST failure atomicity tests.
17. Add POST-on-date tests.
18. Add quantity/costing equivalence tests.
19. Run Inventory tests.
20. Run SQL Server concurrency tests.
21. Run Production regression tests.
22. Full solution build/test.
```

---

# 64. Code Review Stop Conditions

Stop implementation and do not merge if any of these occurs:

```text
Future Save creates IvTrxHistory
Future Save creates/changes IvBalLoc
Future Save creates valuation facts
Future Save changes FIFO layers
Future Save changes PO posted received/return qty
Future POST can commit before trx date
Production IP/PRTN behavior changes unexpectedly
Stock Count begins allowing future dates
rollback closed-period guard is weakened
INV-03/04/05 tests fail
FIFO/Moving Average/Standard Cost tests change expected values
```

Any such result means the implementation crossed the intended boundary.

---

# 65. Manual UAT Matrix

## UAT A — Future MR

```text
13-Oct
Create MR dated 14-Oct
Save
```

Expected:

```text
NEW
no stock increase
no history
no valuation
```

POST on 13-Oct:

```text
blocked
```

POST on 14-Oct:

```text
normal posting
```

---

## UAT B — Future MI

```text
13-Oct
Create MI dated 14-Oct against valid current stock
Save NEW
```

Expected:

```text
no stock decrease
```

POST on 13-Oct:

```text
blocked
```

POST on 14-Oct with sufficient current stock:

```text
normal posting
```

POST on 14-Oct after stock was consumed:

```text
insufficient stock
```

---

## UAT C — Future GR

```text
13-Oct
Create GR dated 14-Oct
Save NEW
```

Expected:

```text
PO received qty unchanged
stock unchanged
```

POST on 13-Oct:

```text
blocked
```

POST on 14-Oct:

```text
revalidate PO
post stock
update PO qty
value stock
```

---

## UAT D — Future TR

```text
13-Oct
Create transfer dated 14-Oct
Save NEW
```

Expected:

```text
source unchanged
destination unchanged
```

POST on 14-Oct:

```text
revalidate source
normal transfer
cost preserved by existing transfer logic
```

---

## UAT E — Future ADJ

```text
13-Oct
Create ADJ dated 14-Oct
Save NEW
```

Expected:

```text
balance unchanged
cost state unchanged
```

POST on 14-Oct:

```text
normal ADJ valuation
```

---

## UAT F — Month Close

```text
August transaction
August CLOSED
```

Expected:

```text
POST/ROLLBACK still blocked
```

No change from current behavior.

---

# 66. Final Safety Proof

The implementation is acceptable because the change occurs before the stock/cost engines:

```text
DOCUMENT LIFECYCLE
    |
    |  future NEW now allowed
    v
POST DATE GATE
    |
    |  future effective date rejected
    v
EXISTING LOCKED STOCK VALIDATION
    |
    v
EXISTING QTY MUTATION
    |
    v
EXISTING HISTORY
    |
    v
EXISTING STOCK LEDGER V2 VALUATION
```

Nothing below the POST DATE GATE is redesigned.

Therefore:

```text
Qty calculation        unchanged
BalLoc update math     unchanged
stock chronology       unchanged
rollback chronology    unchanged
period close           unchanged
FIFO                   unchanged
Moving Average         unchanged
Standard Cost          unchanged
V2 exact reversal      unchanged
Production logic       intentionally outside scope
```

---

# 67. Definition of Done

The plan is implemented correctly only when all of these are true:

1. Normal Inventory future-dated documents can be saved as `NEW`.
2. They can be edited while `NEW`.
3. Save/Update writes no stock movement.
4. POST before transaction date is blocked server-side.
5. The final future-date decision uses the authoritative locked batch.
6. The company-local `ICurrentDateService.Today` is used.
7. POST on transaction date follows the unchanged existing posting engine.
8. Current stock is re-read/revalidated at POST.
9. INV-03 remains unchanged.
10. INV-04 remains unchanged.
11. INV-05 remains unchanged.
12. Inventory period-close behavior remains unchanged.
13. Rollback behavior remains unchanged.
14. GR/VR PO posted quantities do not change on failed future POST.
15. MR/CR expiry uses transaction date consistently.
16. Stock Count still rejects future CountDate.
17. Production shared posting paths show no regression.
18. No automatic lot-number semantics are changed by this task.
19. No database schema change is introduced.
20. FIFO / Moving Average / Standard Cost results remain equivalent.
21. Future-draft-vs-same-day-created equivalence tests pass.
22. SQL Server concurrency tests pass.
23. Full solution build passes.
24. Relevant Inventory + Production test suites pass.

---

# 67A. Final Repository Review — 2026-10-13

A last verification was performed against:

```text
productionv2
HEAD d9a67ced8ea414463b80160a6e5e68e8cfdcc8fe
tree 65f5277f7243c21e3eee0e093b80046195120ef4
```

The branch had not changed from the prior deep review.

The last pass re-confirmed:

```text
IvStockMovementRules.ResolveMovementDate
    still rejects future movement dates.

PostInventoryMRCoreAsync
PostNonStockGoodsReceiptCoreAsync
PostInventoryMICoreAsync
PostInventoryADJCoreAsync
PostInventoryTRAsync
    remain the relevant normal Inventory posting locations.

Production Material Issue
    still calls PostStockOutInTransactionAsync(..., IP).

Production Stock Return
    still calls PostStockInInTransactionAsync(..., PRTN).

Stock Count
    still uses PostStockAdjustmentInTransactionAsync(...)
    and independently rejects future CountDate.

InventoryValuationService
    still rejects backdated valuation behind later facts.

FIFO
    still orders layers by ReceiptEffectiveAt.
```

Final review conclusion:

```text
The safest implementation is the locked-batch POST date gate only.
Do not add a second policy gate in BeginStandaloneLedgerAsync.
Do not modify quantity, chronology, valuation, rollback, Production,
Sales, Stock Count, or lot-number allocation semantics.
```

**Final review score: 10/10 — APPROVED FOR IMPLEMENTATION**, subject to the mandatory build/test/SQL Server regression gates in this document.

---

# 68. Final Approval

## APPROVED FOR IMPLEMENTATION

This deep-reviewed version is intentionally narrower than the previous plan.

It implements only:

```text
future date allowed while document is NEW
+
future date forbidden from POST until company business date reaches it
+
MR/CR expiry validation aligned to transaction date
```

It does **not** redesign:

```text
stock balance
stock history
costing
FIFO
moving average
standard cost
rollback
month end
Production
Sales
lot numbering
database schema
```

That boundary is the reason this version is considered safe for `productionv2`.

**Agent instruction:** If implementation requires changing any of the excluded core quantity/costing algorithms to make the feature work, stop and re-review rather than expanding scope.
