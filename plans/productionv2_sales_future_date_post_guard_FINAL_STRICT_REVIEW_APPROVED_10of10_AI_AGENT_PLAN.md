# productionv2 — Sales Future-Date POST Guard
## FINAL REVIEWED AI CODING AGENT PLAN

**Repository:** `mokth/net10projectTemplate`  
**Branch reviewed:** `productionv2`  
**Repository head verified:** `d9a67ced8ea414463b80160a6e5e68e8cfdcc8fe`  
**Review status:** **FINAL STRICT REVIEW — APPROVED FOR IMPLEMENTATION — 10/10**  
**Scope:** Sales DO / Invoice / Credit Note / Debit Note posting only  
**Database migration:** None  
**FIFO / CostLayer / quantity algorithm changes:** None  
**Shipment allocation rule changes:** None  
**Rollback rule changes:** None  

---

# 1. Objective

Implement this business rule:

> A Sales document may be created, edited, deleted, and prepared with a future document date, but it must not become POSTED before the company-local business date reaches the document date.

Required behavior:

| Action | Future-dated Sales document |
|---|---|
| New / Save | ✅ Allow, subject to existing validations |
| Edit | ✅ Allow, subject to existing validations |
| Delete NEW | ✅ Existing delete rules only |
| Prepare DO / direct-Invoice shipment | ✅ Allow |
| Edit shipment / lots | ✅ Existing rules |
| POST | ❌ Block while `DocumentDate > BusinessDate` |
| POST when `DocumentDate <= BusinessDate` | ✅ Continue existing posting flow |
| ROLLBACK | ✅ Existing rollback safeguards only; do not add a future-date rollback block |

Example:

```text
Business date : 13/10/2026
DO date       : 14/10/2026

Save/Edit/Shipment   ALLOW
POST                 BLOCK

Business date : 14/10/2026
POST                 ALLOW after all existing validation
```

---

# 1A. Final Repository Verification

The final review re-checked the current `productionv2` head and confirmed the implementation target has not changed since the previous review.

Current branch head at review time:

```text
d9a67ced8ea414463b80160a6e5e68e8cfdcc8fe
```

The following Sales mutator services were specifically audited for alternate posting/status paths:

- `SaDoService`
- `SaInvoiceService`
- `SaCdnService`
- `SaDocApplicationService`
- `SaDeliveryRequestService`
- `SaDeliveryRequestFulfilmentService`
- `SaAllocationReconciliationService`
- `SaSoService`
- `SaQtService`

Confirmed:

- only `SaDoService` calls `PostStockOutInTransactionAsync` for DO;
- only `SaInvoiceService` calls `PostStockOutInTransactionAsync` for Invoice;
- only `SaCdnService` calls `PostStockInInTransactionAsync` for CN ReturnStock;
- only those same three services set DO / Invoice / CN-DN to POSTED;
- Delivery Request / fulfilment / document-application services only consume or require POSTED states; they do not bypass these posting entry points;
- DO Force Close requires the DO to already be POSTED and therefore cannot bypass the future POST guard.

If the branch head changes before implementation begins, the coding agent must re-check these exact entry points before editing.

---

# 2. Current Repository Findings

Current `productionv2` was reviewed through:

- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`
- `ErpWeb.Core/Sales/SaCdnService.cs`
- `ErpWeb.Core/Sales/ISaDoService.cs`
- `ErpWeb.Core/Sales/ISaInvoiceService.cs`
- `ErpWeb.Core/Sales/ISaCdnService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentAllocator.cs`
- `ErpWeb.Core/Inventory/IvSpFifoEligibility.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.Chronology.cs`
- `ErpWeb.Core/Inventory/IvStockMovementRules.cs`
- `ErpWeb.Core/Services/CurrentDateService.cs`
- Sales entry/list UI posting flows
- existing Sales transaction test suites

Confirmed:

1. DO/direct-Invoice shipment eligibility currently uses:

```text
Stock.TransDate <= SalesDocument.DocumentDate
```

2. Candidate discovery, FIFO allocation, manual lot replacement and post-time shipment validation all use the document date.

3. DO and Invoice currently have no authoritative:

```text
DocumentDate <= _dates.Today
```

check during POST.

4. `Credit Note + ReturnStock=true` can construct a Customer Return inventory batch with:

```csharp
TrxDtTime = cdn.DocDate.Date
```

and post it through inventory stock-in.

5. `ICurrentDateService` already exists in DO, Invoice and CDN services and returns company-local `Today` using `Company.TimeZoneId`, with Malaysia fallback.

6. Existing bulk POST behavior is **sequential and stop-on-first-failure**:
   - previously successful items remain posted;
   - first failed item fails;
   - later selected items become `Not attempted`.

This behavior MUST remain unchanged.

---

# 3. Key Safety Proof

Do NOT change FIFO or shipment eligibility.

Existing shipment/stock posting already requires:

```text
Stock.TransDate <= DocumentDate
```

The new POST guard adds:

```text
DocumentDate <= BusinessDate
```

Therefore any successful Sales stock POST guarantees:

```text
Stock.TransDate <= DocumentDate <= BusinessDate
```

So future stock cannot be physically consumed by a successful DO/direct-Invoice POST.

This achieves the required safety without changing shipment preparation, FIFO, costing, reservations or quantity calculations.

---

# 4. Important Intentional Behavior: Future Shipment Reservation Remains

The current shipment batch/detail mechanism also acts as a reservation while the Sales document remains NEW.

Therefore a future-dated DO/Invoice may reserve eligible stock before its posting date.

This plan intentionally leaves that behavior unchanged because the requested policy is:

> Allow preparation; block only the actual POST.

This may reduce stock available to other draft shipments while the future document is reserved, but it does **not** alter physical stock quantity or costing.

Do NOT change reservation logic as part of this plan.

If the business later wants “future documents must not reserve current stock”, handle that as a separate policy enhancement.

---

# 5. Add One Neutral Shared POST-Date Guard

## New file

`ErpWeb.Core/Transactions/TransactionPostingDateGuard.cs`

Do not add this rule to Inventory-specific classes and do not overload `TransactionLifecycleGuard`, whose current responsibility is archive/lifecycle handling.

Recommended implementation:

```csharp
namespace ErpWeb.Core.Transactions;

internal static class TransactionPostingDateGuard
{
    public static string? FutureDateError(
        DateTime documentDate,
        DateTime businessDate)
    {
        var document = documentDate.Date;
        var business = businessDate.Date;

        return document > business
            ? $"Transaction date {document:dd/MM/yyyy} is after the business date {business:dd/MM/yyyy}."
            : null;
    }
}
```

Requirements:

- compare `.Date` only;
- same-day posting is allowed regardless of time component;
- no `DateTime.Today`;
- no `DateTime.UtcNow.Date`;
- no DB/schema change;
- keep the helper `internal`; `ErpWeb.Core.csproj` already exposes internals to `ErpWeb.Tests`.

---

# 6. Resolve Business Date Once Per Bulk POST

This is a correction from the first plan.

`CurrentDateService.Today` internally resolves company timezone using its own DbContext.

Do not invoke `_dates.Today` after a Sales row has been locked inside `PostOneAsync`, because that would cause an unnecessary secondary DB read while a posting transaction is holding locks.

Instead:

1. validate write context;
2. validate permission where the outer service currently does so;
3. normalize/check selected items;
4. resolve once:

```csharp
var businessDate = _dates.Today.Date;
```

5. pass that value into every `PostOneAsync`.

Benefits:

- one company-local date for the whole bulk operation;
- deterministic behavior if posting happens around midnight;
- no extra timezone/company lookup while row locks are held;
- easy deterministic testing.

**Intentional snapshot rule:** one `PostAsync` invocation uses one business-date snapshot. If a bulk operation starts before midnight and continues past midnight, it must not change date rules half-way through the selection. A document that becomes eligible only after midnight is posted on the next user attempt.

In the current repository, `_dates.Today` is the company-local calendar date resolved from `Company.TimeZoneId`; this plan uses that existing value as the Sales posting business date. Do not invent a new persisted/configurable business-date field as part of this change.

This is intentionally the repository's existing **company-local calendar date**, not a new accounting-period date or manually configurable operational date. Period-close validation remains a separate existing rule.

Do this separately in:

- `SaDoService.PostAsync`
- `SaInvoiceService.PostAsync`
- `SaCdnService.PostAsync`

Do not change `ICurrentDateService`.

---

# 7. Delivery Order

## File

`ErpWeb.Core/Sales/SaDoService.cs`

## `PostAsync`

After current:

- write-context validation;
- POST permission check;
- request normalization;
- empty/max-selection checks;

add:

```csharp
var businessDate = _dates.Today.Date;
```

Pass it to `PostOneAsync`.

## `PostOneAsync`

Extend the private signature to accept `businessDate`.

Keep the current validation order:

1. lock authoritative DO;
2. not found;
3. archived/deleted check;
4. status must be NEW;
5. RowVersion/concurrency validation;
6. **NEW future-date guard**;
7. load details;
8. existing processing.

Immediately after RowVersion validation:

```csharp
if (TransactionPostingDateGuard.FutureDateError(
        deliveryOrder.DoDate,
        businessDate) is string futureDateError)
{
    await tx.RollbackAsync(cancellationToken);
    return SaDoPostingItemResult.Failed(doNo, futureDateError);
}
```

Use the existing simple `Failed(doNo, reason)` overload.

**Do not add a new public reason code.**

The UI does not branch on `ReasonCode`; the existing error message already flows correctly to entry and list pages.

The guard MUST occur before:

```csharp
db.Entry(deliveryOrder).Collection(...).LoadAsync(...)
BuildSoToDoAllocations(...)
_docApplication.AllocateSOToDOAsync(...)
ValidateShipmentForPostAsync(...)
BeginPostingInTransactionAsync(...)
PostStockOutInTransactionAsync(...)
```

Therefore rejected future POST must not alter:

- SO allocation/delivery quantity;
- DO detail allocated quantities;
- shipment reservation;
- `IvBalLoc`;
- `IvTrxHistory`;
- stock ledger;
- costing;
- DO status/posted audit fields.

The guard applies even when the DO has no stock-controlled lines.

---

# 8. Sales Invoice

## File

`ErpWeb.Core/Sales/SaInvoiceService.cs`

## `PostAsync`

Resolve:

```csharp
var businessDate = _dates.Today.Date;
```

once after current request/permission/selection validations and before the loop.

Pass it to `PostOneAsync`.

## `PostOneAsync`

Keep validation priority:

1. lock authoritative invoice;
2. archived check;
3. status NEW;
4. expected RowVersion when supplied;
5. **future-date guard**;
6. commercial readiness;
7. source allocation;
8. shipment/stock posting;
9. mark POSTED.

Guard:

```csharp
if (TransactionPostingDateGuard.FutureDateError(
        invoice.InvDate,
        businessDate) is string futureDateError)
{
    await tx.RollbackAsync(cancellationToken);
    return SaInvoicePostingItemResult.Failed(invNo, futureDateError);
}
```

The rule applies to:

- direct-stock Invoice;
- non-stock Invoice;
- Invoice sourced from SO;
- Invoice sourced from POSTED DO / `LinkDo=true`.

For linked DO lines there is no second stock issue, but the Invoice itself still cannot become POSTED before `InvDate`.

Do not change:

- `AllocateInvoiceLinesAsync`;
- shipment logic;
- e-Invoice logic;
- commercial readiness;
- accounting mappings.

A future Invoice stays NEW and therefore remains naturally unavailable for downstream workflows that require POSTED status.

---

# 9. Credit Note / Debit Note

## File

`ErpWeb.Core/Sales/SaCdnService.cs`

## `PostAsync`

After current selection validation and before the bulk loop:

```csharp
var businessDate = _dates.Today.Date;
```

Pass to `PostOneAsync`.

Note: CDN permission is type-specific and currently validated inside `PostOneAsync`; preserve that order.

## `PostOneAsync`

Preserve:

1. current lock order;
2. document not found;
3. CN/DN POST permission;
4. archive check;
5. status NEW;
6. RowVersion;
7. **future-date guard**;
8. invoice remaining/readiness;
9. optional CustomerReturn batch;
10. stock-in;
11. mark POSTED.

Guard:

```csharp
if (TransactionPostingDateGuard.FutureDateError(
        cdn.DocDate,
        businessDate) is string futureDateError)
{
    await tx.RollbackAsync(cancellationToken);
    return SaCdnPostingItemResult.Failed(docNo, futureDateError);
}
```

This must execute before any CustomerReturn batch is created.

Critical protected path:

```text
Future CN + ReturnStock=true
        ↓
POST rejected
        ↓
NO future CustomerReturn IvTrxBatch
NO IvBalLoc increase
NO IvTrxHistory
NO costing/ledger mutation
```

The same POST-date rule applies to:

- Credit Note without stock return;
- Credit Note with stock return;
- Debit Note.

This provides consistent Sales document lifecycle behavior.

---

# 9A. Sales Posting Entry-Point Audit

Current Sales entry/list UI posting actions route through:

- `SaDoService.PostAsync` → `PostOneAsync`
- `SaInvoiceService.PostAsync` → `PostOneAsync`
- `SaCdnService.PostAsync` → `PostOneAsync`

No separate Sales posting service exists in the current `ErpWeb.Core/Sales` tree.

Therefore the authoritative guard belongs in these three locked-document `PostOneAsync` paths.

Do not implement the rule only in Razor/UI predicates, because service callers and list/bulk posting must receive the same protection.

Do not implement the rule only in `IvSpShipmentService`, because non-stock DO/Invoice/CN/DN must also obey the future POST rule.

---

# 10. Explicitly Out of Scope

Do not change:

### Quotation
No POST lifecycle in the current service.

### Sales Order
No POST lifecycle in the current service.

### Delivery Request
Uses draft/release/cancel lifecycle, not Sales POST.
Do not add this new rule to `ReleaseAsync` in this change.

### Generic Inventory posting core
Do not add the Sales date rule to:

- `IvInventoryPostingService`
- `StockPostingCoordinator`
- `StockPeriodGuard`

Doing so would silently change Inventory, Procurement and Production posting behavior beyond this task.

A separate Inventory future-date implementation may reuse `TransactionPostingDateGuard` later, but this Sales change must remain isolated.

### DO Force Close
Do not change `ForceCloseOneAsync`. It already requires the DO to be POSTED, so a future NEW DO cannot use Force Close to bypass the new POST-date rule.

### Production
No Production posting behavior is to change.

---

# 11. Rollback — Preserve Exact Existing V2 / Legacy Semantics

Do NOT add this:

```csharp
if (DocumentDate > BusinessDate)
    block rollback;
```

Do not pass the new Sales `businessDate` into rollback methods.

Keep untouched:

- `SaDoService.RollbackOneAsync`
- `SaInvoiceService.RollbackOneAsync`
- `SaCdnService.RollbackOneAsync`
- `StockBusinessTime.NowAsync`
- `InventoryStockPostingCommandFactory`
- inventory stock rollback cores;
- stock ledger reversal;
- existing period validation;
- existing legacy chronology validation;
- allocation reversal;
- costing/quantity reversal.

### Current V2 behavior that MUST NOT be changed

DO / Invoice / CN stock rollback currently obtains:

```csharp
StockBusinessTime.NowAsync(...)
```

and passes that current stock-business time as the V2 reversal `effectiveAt`.

`InventoryStockPostingCommandFactory` uses that reversal effective time for the new REVERSAL posting unless replaying an already-created reversal.

The V2 posting coordinator validates the reversal's current `effectiveAt` against the period guard.

When a V2 `StockPostingContext` is present, the inventory rollback core uses that posting context and does not run the legacy `ValidateRollbackChronologyAsync` branch.

### Current legacy / no-V2 behavior

When no V2 posting context exists, the rollback core falls back to the inventory batch transaction date and runs the existing legacy rollback chronology validation.

### Required policy for this task

Preserve BOTH paths exactly.

This future-date Sales task must not redesign rollback date semantics, V2 reversal effective-date semantics, period-close semantics, or chronology.

Reason:

A legacy transaction that was incorrectly posted in the future must remain reversible whenever the repository's existing reversal safeguards allow it. The new future-date rule is a **forward POST gate**, not a rollback gate.

---

# 12. UI — No Functional Redesign

Do NOT:

- set date editor `MaxDate=today`;
- block future Save;
- block future Edit;
- block future Delete;
- disable shipment preparation;
- introduce JavaScript/browser-date checks;
- use the client machine date as authority.

Keep current POST buttons/list actions.

The server returns:

```text
Transaction date 14/10/2026 is after the business date 13/10/2026.
```

Existing entry/list result handling already displays service failure messages.

Server-side enforcement therefore protects:

- entry page POST;
- list/bulk POST;
- future callers;
- APIs/services that bypass UI button state.

Optional UI hints are outside this implementation.

---

# 13. Bulk POST Semantics — Preserve Exactly

Current DO, Invoice and CDN POST loops use:

```text
process item
if failed:
    stop = true
remaining items:
    Not attempted
```

Do not convert bulk posting to “continue after error”.
Do not make all selected documents one database transaction.

Example:

```text
BusinessDate = 13 Oct

Selection:
A = 13 Oct
B = 14 Oct
C = 13 Oct
```

Expected:

```text
A → POSTED
B → Failed: future date
C → Not attempted
```

If B is selected first:

```text
B → Failed: future date
A/C → Not attempted
```

Earlier successfully committed documents remain committed because this is existing service behavior.

---

# 14. Tests — Shared Guard

## New test file

`ErpWeb.Tests/Transactions/TransactionPostingDateGuardTests.cs`

Test:

1. document date before business date → allowed;
2. same date → allowed;
3. document date after business date → returns error;
4. different time components on same calendar date → allowed;
5. error contains both document and business dates.

---

# 15. DO Tests

## File

`ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`

Reuse existing:

```csharp
FixedCurrentDateService(FixedToday)
```

Add cases:

### DO-01
Future DO can Save as NEW.

### DO-02
Future DO can Edit while NEW.

### DO-03
Future DO can create/retain shipment under existing shipment rules.

Use current-dated available stock for this test so it proves preparation is allowed without depending on seeded corrupt/future stock.

### DO-04
Future stock-controlled DO POST is rejected.

For the strongest regression test, reproduce the original defect directly:

```text
Fixed business date     = 13 Oct
Seeded IvBalLoc date    = 14 Oct
DO date                 = 15 Oct
```

The seeded future balance is intentional test setup for a legacy/corrupt/bypass scenario; normal Inventory entry currently prevents creating such a future movement.

Confirm existing shipment preparation can still select/allocate the 14 Oct pile for the 15 Oct DO, then call POST and assert the new Sales date guard rejects it before physical posting.

Assert:

- result fails with future-date message;
- DO status remains NEW;
- `IvBalLoc.StdQty` unchanged;
- shipment batch remains NEW;
- no `IvTrxHistory`;
- no SO allocation/write-back occurs;
- no posted stock ledger operation if the fixture has V2 ledger enabled.

### DO-05
Future non-stock DO POST is also rejected.

This proves the guard is a document lifecycle rule, not merely a shipment guard.

### DO-06
DO dated exactly `FixedToday` posts normally.


### DO-06A
Prove the same saved future DO becomes postable when the business date catches up.

Recommended test-only approach:

- allow the test SUT factory to accept an optional `ICurrentDateService`;
- save/prepare the DO using `FixedCurrentDateService(FixedToday)`;
- create a second SUT over the same test database using `FixedCurrentDateService(FixedToday.AddDays(2))`;
- POST the unchanged DO;
- verify normal posting succeeds subject to the existing stock/shipment rules.

Do not add a mutable clock to production code.

### DO-07
Past date in an otherwise valid/open period continues normal posting behavior.

### DO-08
Existing POST→ROLLBACK end-to-end quantity restoration test remains green.

### DO-09
Future-dated DO with a stale supplied RowVersion still returns the existing concurrency failure first. This confirms the new guard does not change current optimistic-concurrency precedence.

### DO-10 — bulk order
Valid DO first, future DO second, valid DO third:

```text
first  = Posted
second = Failed future date
third  = Not attempted
```

---

# 16. Invoice Tests

## File

`ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs`

Add:

### INV-01
Future Invoice Save succeeds subject to existing normal validations.

### INV-02
Future Invoice Edit succeeds.

### INV-03
Future direct-stock Invoice shipment can be prepared.

### INV-04
Future direct-stock Invoice POST fails with zero stock/allocation side effects.

Also reproduce the original future-stock scenario:

```text
Fixed business date     = 13 Oct
Seeded IvBalLoc date    = 14 Oct
Invoice date            = 15 Oct
```

Confirm the current shipment allocator can prepare the future Invoice from that pile, then verify POST is rejected before inventory mutation.

Assert:

- status NEW;
- `IvBalLoc.StdQty` unchanged;
- SP batch NEW;
- no `IvTrxHistory`;
- allocation state unchanged.

### INV-05
Future non-stock Invoice POST fails.

### INV-06
Future Invoice sourced from POSTED DO / `LinkDo=true` also fails to POST even though no second shipment is required.

### INV-07
Invoice dated exactly `FixedToday` posts normally.


### INV-07A
Using the same test-database / second-fixed-clock technique, prove a previously future Invoice becomes postable once the company-local business date reaches or passes `InvDate`.

### INV-08
Existing POST→ROLLBACK tests remain green.

### INV-09
Bulk stop-on-first-failure behavior is preserved.

---

# 17. CN / DN Tests

## File

`ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs`

Add:

### CDN-01
Future CN Save succeeds subject to existing validations.

### CDN-02
Future CN without stock return cannot POST.

### CDN-03
Future Debit Note cannot POST.

### CDN-04
Future `Credit Note + ReturnStock=true` cannot POST.

Assert **before/after**:

- CN status remains NEW;
- no CustomerReturn `IvTrxBatch` is created for the CN;
- `IvBalLoc` quantity unchanged;
- no `IvTrxHistory`;
- no stock ledger posting.

### CDN-05
CN/DN dated exactly `FixedToday` retains normal posting behavior.

### CDN-06
Existing CustomerReturn POST→ROLLBACK tests remain green.

### CDN-07
Future-dated CN/DN with a stale supplied RowVersion still returns the existing concurrency failure first.

### CDN-08
Bulk stop-on-first-failure behavior is preserved.

---

# 18. Existing Validation Must Still Apply

“Future date allowed for Save/Edit” does NOT mean bypassing other existing business rules.

Do not change failures caused by:

- closed period where already enforced;
- invalid/missing currency rate;
- invalid customer;
- invalid item/warehouse;
- shipment incompleteness;
- insufficient stock;
- stale RowVersion;
- permissions;
- e-Invoice structural restrictions;
- pricing/readiness validation;
- source document limits.

The only new behavior is:

```text
POST rejects document date > company-local Today
```

---

# 19. Regression Suite

Run at minimum:

```text
ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs

ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockAdjustmentPostingServiceTests.cs
```

Also run the full `ErpWeb.Tests` project before completion.

Any failure involving:

- FIFO;
- quantity balance;
- CostLayer/valuation;
- shipment reservation;
- posting history;
- rollback;
- stock ledger;
- concurrency;
- SO/DO/Invoice allocation;

must be investigated before implementation is considered complete.

---

# 20. MUST NOT CHANGE

The AI coding agent must not modify:

- `IvSpShipmentAllocator`;
- `IvSpFifoEligibility`;
- FIFO lot ordering;
- shipment stock eligibility;
- reservation quantity formulas;
- `IvBalLoc` arithmetic;
- `IvTrxHistory` structure;
- CostLayer/FIFO costing;
- valuation;
- SO→DO allocation calculation;
- Invoice allocation calculation;
- CN remaining calculation;
- stock ledger schema;
- period-close rules;
- rollback chronology;
- rollback effective-date behavior;
- document numbering;
- permissions;
- DB schema/migrations.

---

# 21. Implementation Sequence

1. Add `TransactionPostingDateGuard`.
2. Add shared guard unit tests.
3. Update `SaDoService.PostAsync` to resolve `businessDate` once.
4. Pass it to DO `PostOneAsync`.
5. Add DO guard after RowVersion validation and before details/allocation.
6. Update Invoice `PostAsync` and `PostOneAsync` identically.
7. Update CDN `PostAsync` and `PostOneAsync` identically.
8. Add focused future-date tests.
9. Add bulk stop-on-first-failure tests.
10. Run focused Sales suites.
11. Run Inventory regression suites.
12. Run full `ErpWeb.Tests`.
13. Manually verify:
    - future Save/Edit works;
    - future shipment preparation works;
    - future POST fails;
    - same-day POST works;
    - current rollback behavior is unchanged.

---

# 22. Definition of Done

Implementation is complete only when:

- future DO/Invoice/CN/DN may remain NEW and editable;
- future DO/direct-Invoice shipment may still be prepared;
- no Sales document can POST before its document date;
- failed future POST has zero posting/allocation/costing/quantity side effects;
- future CN ReturnStock cannot create future physical stock;
- same-day and past-open posting still behaves normally;
- rollback is unchanged and remains controlled by existing period/chronology rules;
- bulk POST retains stop-on-first-failure semantics;
- no UI/browser date becomes authoritative;
- no generic Inventory/Production posting behavior changes;
- no DB migration is introduced;
- all new and existing relevant tests pass.

---

# 22A. Final Implementation Review Gate

Before the coding agent reports completion, re-open the actual modified code and verify all of the following:

1. `_dates.Today.Date` is resolved once per `PostAsync` call, before the per-document posting transaction loop.
2. The authoritative persisted document date is used:
   - `SaDo.DoDate`
   - `SaInvoice.InvDate`
   - `SaCdn.DocDate`
3. The guard executes after current status / RowVersion validation but before the first posting/allocation mutation.
4. No Razor/UI-only date check is being relied upon.
5. No future-date check was added to Save/Update/Delete/Shipment.
6. No future-date check was added to rollback.
7. No generic Inventory / Procurement / Production posting core was changed.
8. DO Force Close remains unchanged.
9. Exact future-stock reproduction tests pass with zero physical stock/history mutation.
10. Full relevant regression suite passes.

If any of these are false, implementation is not approved.

---

# 23. Final Safety Invariant

For a successfully posted Sales stock movement:

```text
Stock.TransDate <= DocumentDate <= CompanyBusinessDate
```

For a future document:

```text
NEW / EDIT / DELETE / PREPARE SHIPMENT = existing behavior
POST                                  = BLOCKED
ROLLBACK                              = existing safety rules only
```

This is the smallest current-repository change that closes the future-stock posting gap without disturbing Sales workflow, FIFO, costing, quantity balance, shipment preparation, Production, Procurement, or Inventory core logic.
