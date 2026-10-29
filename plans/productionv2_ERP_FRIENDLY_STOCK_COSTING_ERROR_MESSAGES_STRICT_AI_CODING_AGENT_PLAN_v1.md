# PRODUCTIONV2 — ERP-FRIENDLY STOCK / COSTING / QUANTITY ERROR MESSAGES

FINAL STRICT IMPLEMENTATION PLAN FOR AI CODING AGENT — v1

Status: IMPLEMENTATION-READY PLAN, SUBJECT TO PHASE GATES; NO CODE HAS BEEN CHANGED BY THIS PLAN.
Repository: mokth/net10projectTemplate
Target branch: productionv2 (DO NOT work on default main)
Repository baseline inspected: 371725c0bda74e386a66bfa3c7559c6f227a14dd, 29 Oct 2026.
Target: Normal Malaysian SME ERP users can understand what failed, why, which item/document caused it, and what to do, without reading code or asking an AI/programmer.
Scope: Error communication only, covering stock quantity/costing failures across Inventory, StockLedger, Procurement, Sales shipments, and Production stock/WIP.
Constraint: ZERO INTENTIONAL BUSINESS-BEHAVIOUR CHANGES. NO COSTING / QUANTITY / POSTING / ROLLBACK ALGORITHM CHANGES. NO DATABASE SCHEMA CHANGES. NO NEW TRANSACTION SCREENS.
Suggested execution: A reasoning-capable coding agent in short, review-gated phases. Never implement all phases in one huge context. Save this downloaded file to plans/ in the repository before instructing the coding agent to open it; do not assume it is already committed.

## 0. MANDATE: ERROR PRESENTATION ONLY

The user expressly authorises improving text, structured error transport, read-only error explanation and existing-screen UX. This is NOT approval to fix or redesign inventory/costing business logic, add new transaction flows, change entity schema, or relax a posting/rollback rule. In particular, costing and quantity-balance development may be ongoing in this branch. Do not merge/rebase away concurrent work; inspect the HEAD and avoid touching active costing implementation.

### 0.1 Permitted changes

- Add shared error-presentation contracts, stable presentation reason identifiers, message templates and a presenter/formatter.
- Preserve existing backend technical error codes and messages for logging; propagate additional optional, non-breaking diagnostic metadata at the error boundary where needed.
- Extend relevant existing operation-result DTOs with optional OperatorError / UserMessage properties without removing or reinterpreting current properties.
- Add narrowly scoped, tenant-filtered, read-only lookups for affected item/document context after a business operation has failed, where practical and safe; use an existing read model if available. Never mutate stock during error explanation.
- Reuse/extend current error toasts, panels or popups in existing transaction pages and lists; display individual batch/line failures and optional permission-checked links.
- Add unit, integration, security and UX regression tests, source-error inventory and audit documentation.
- Exceptional narrow allowance: attaching a diagnostic Reason / EventId to an already-thrown StockLedgerError, with the original conditional, exception type, error code, exception text, calculation, and ordering unchanged. This is metadata, not a new validation rule. If unsafe or overlapping a concurrent change, skip detail and show a truthful generic business message.

### 0.2 Forbidden changes (fail the phase immediately)

- Modifying FIFO allocation, FIFO/CostLayer quantity or value, WAC moving-average, Standard Cost, variance, COGS, receipt settlement, purchase cost adjustments, costing projections, replay simulations or production cost absorption.
- Modifying IvBalLoc, stock count correction, item lot/warehouse/status balance writes, Production WIP, quantity calculations, business dates, month-end guards, period close, future-date guard, UOM conversion mathematics.
- Changing any posting/rollback acceptance predicate, dependency rule, execution order, transaction/lock boundary, isolation, concurrency decision, idempotency, database writes, audit trails, business status transitions or partial-success semantics.
- Changing persisted/internal error codes, SQL table schemas, EF migrations, indexes, stored procedures, configuration meaning, approvals, permissions, or tenant selection.
- Automatically rolling back a dependent transaction, silently correcting balances/costs, changing FIFO layers to work around a failure, or recommending a cost-method switch as a workaround.
- Catching/reclassifying an exception as success, swallowing an exception, retrying a posting on an uncertain outcome, or showing false "No changes were made" after a partially successful batch.
- Regex/text parsing of low-level English error messages as a general classification mechanism. Never infer document ownership from unverified event IDs or loosely join by item alone.
- Unrelated Sales/Procurement/Production redesign, new transaction screens, or premature internationalisation redesign.

Key testable invariant: For the same initial database state + operation + concurrency schedule, old and new implementations return the same permitted/blocked decision, update exactly the same business records, make the same status transitions, and have the same quantity/cost/valuation results. Only the outward error message/metadata/visualisation may differ. Confirm with snapshot/diff tests, not assumption.

## 1. VERIFIED REPOSITORY FINDINGS: DO NOT GUESS THE FLOW

These are specific, previously inspected paths at the pinned baseline; at the start of every phase re-open current HEAD and record any drift. Full repository exhaustiveness is not yet established: Phase 0 must produce an error-site census.

| Role | Actual source | Finding / implementation consequence |
|---|---|---|
| Core stable error codes | `ErpWeb.Core/StockLedger/StockLedgerErrors.cs` | `StockLedgerErrorCodes` contains 21 codes; `StockLedgerError(Code, Message, Retryable)` and `StockLedgerException.Error` already preserve structured data internally. Do not rename or change them. |
| Exact reported GRN error | `ErpWeb.Core/StockLedger/StockReplayPhysicalSimulator.cs`, `EnsureNonnegative` | Throws `INSUFFICIENT_BASE_QTY` with `Physical replay would make the stock slice negative at event {eventId} while {phase}.` This legitimate historical-negative-stock block MUST remain. |
| Replay timeline | `ErpWeb.Core/StockLedger/StockReplayPhysicalTimelineReader.cs` | Positive event IDs correspond to `IvTrxHistory.Id`, negative IDs can represent stock-count generations. Event ID does not mean Batch No. Events are ordered by effective time, class, posting sequence and identity. |
| Rollback planner | `ErpWeb.Core/StockLedger/StockReplayPhysicalPlanner.cs`, `BuildRemovalAsync` | Simulates removal and later events. Do not rewrite planning/projections or historical validation. |
| GRN orchestration | `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`, `RollbackAsync` | Delegates to `IIvInventoryPostingService.RollbackAsync`. Preserve API/action semantics. |
| Posting/rollback dispatch | `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`, `DispatchAsync`, `RollBackGoodsReceiptAsync` | Returns one per-batch failure; catches `StockLedgerException` but converts to `ex.Error.Message`, losing code + retryability before UI. Main boundary to fix additively. |
| Per-batch results | `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs` | `IvInventoryPostingBatchResult` only exposes `ErrorMessage` + success + batch + operation ID; `FromBatches()` concatenates raw `Batch {N}: ...`. Preserve `SucceededCount`, `FailedCount`, ordered `Batches`, mixed-outcome semantics. |
| GRN result wrapper | `ErpWeb.Core/Inventory/IIvGoodsReceiptService.cs` | `IvGoodsReceiptOperationResult.OkPosting()` wraps the posting result and passes its string directly. Preserve Posting, status/count properties; add operator error without changing success/fail. |
| GRN UI | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.cs`, `ConfirmActionAsync`; corresponding `.razor` | `ErrorMessage = result.ErrorMessage` and a plain toast show the raw backend text. No need for a new screen. |
| Existing validation UX | `ErpWeb.Core/Services/ValidationMessageFormat.cs`; `ErpWeb.UI/Components/Pages/PageBase.cs` | Field/line dictionary is authoritative, deterministic ordering; do not replace/lose any validation entries. Sales DO/Invoice pages already use `BuildValidationMessage`. |
| Existing costing drill-down | `ErpWeb.UI/Inventory/Inquiry/CostingSourceNavigationResolver.cs` | Has routes to GRN, MR/MI, transfers, DO, invoice, IP, Daily Production, FG, etc. Only generate allowed/verified navigation links; check access per target. |
| Existing costing diagnostic screen | `ErpWeb.UI/Inventory/Inquiry/IvCostingCenter.razor[.cs]` | Exists with health/trace/backdate tools and an advanced section. Reuse, do not add a transaction page. |
| FIFO/WAC/Standard diagnostic sources | `ErpWeb.Core/StockLedger/StockReplayFifoLayerAllocator.cs`, `StockReplayWacSimulator.cs`, `StockReplayStandardSimulator.cs`, `InventoryValuationService.cs`, `StockCostMethodResolver.cs`, `ItemStandardCostResolver.cs` | Multiple operator-incomprehensible messages; distinguish real shortage, missing cost, invalid evidence and unsupported replay. |
| Backdated historical GRN path | `ErpWeb.Core/Inventory/IvInventoryPostingService.{WacReplay,FifoReplay,StandardMrReplay}.cs` | Some failures explicitly mean an unsupported combination of Production issue and Purchase Invoice settlement, not broken data. Do not say "stock is wrong" for an unsupported feature. |
| Purchase cost dependencies | `ErpWeb.Core/Purchase/PurchaseReceiptCostSettlementService.cs`, `PurchaseCostAdjustmentPostingService.cs`, `PurchaseCdnCostAdjustmentService.cs` | Purchase Invoice settlement, CN/DN and FIFO variance failures can bubble into user actions. |
| Production stock/WIP | `ProductionMaterialIssueService.Rollback.cs`, `ProductionOutputService.{Posting,Rollback}.cs`, `ProductionFinishedGoodReceiptService.Posting.cs`, `ProductionStockReturnService.Posting.cs`, `ProductionWorkOrderTransferService.Rollback.cs`, `ProductionPostingInvariant.cs` | Has technical "sealed", "posting link", "lineage", WIP and cost invariant failures exposed via result messages or `ex.Message`. |
| Sales consumption | `ErpWeb.Core/Sales/SaDoService.cs`, `SaInvoiceService.cs`; `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`, `SaInvoice.razor.cs`, their list pages | DO/Invoice stock posting may expose technical valuation errors. Preserve existing validation dictionaries and business errors. |
| UI raw exception samples | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.cs`, `PrProductionStockReturnEntry.razor.cs`, `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` | At least some code paths assign `ex.Message` directly to a UI error. Replace only the user-facing error display for scoped operations. |
| Existing tests | `ErpWeb.Tests/Inventory/Transaction/StockReplayPhysicalSimulatorTests.cs` | Includes tests for negative intermediate stock; must remain green unchanged. |

Start-of-work source links:

- https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/StockLedger/StockReplayPhysicalSimulator.cs
- https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Inventory/IvInventoryPostingService.cs
- https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Inventory/IIvInventoryPostingService.cs
- https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.cs

## 2. PRODUCT REQUIREMENTS: WHAT AN ORDINARY USER MUST SEE

### 2.1 Message shape

For every failed stock/costing/quantity-related POST / ROLLBACK / SAVE / DELETE / CANCEL / STOCK COUNT / HISTORICAL REPLAY where an ordinary ERP user can reasonably act on the message:

- Clear headline: "Cannot roll back Goods Receipt 1."
- Specific accurate reason: "A later stock issue would leave insufficient stock on 02/10/2026."
- Verified affected record (optional when unavailable): item + description, warehouse, location, lot, status, affected document and date.
- Numbers if proven: available, required, shortage, unit of measure. Never fake decimals, currencies, costing, stock identity or date.
- Next step: "Review Material Issue 2 in Stock Movement Inquiry", "Reload the document", "Check the approved currency rate", "Ask an authorised inventory manager to review the costing records."
- Reference ID: correlates to server logs without needing users to recognise history IDs, stack traces, FIFO/WAC internals.
- Optional details and permission-gated Open related document / View movements links. No link if ownership/route/permission cannot be proven.

Style: English, Malaysian ERP terminology; GRN / Goods Receipt, Purchase Invoice, Delivery Order, Stock Count, Work Order, Production Stock. Use dd/MM/yyyy for operator dates, appropriate invariant/locale-aware quantity formatting with base UOM, preserve exact arithmetic precision in diagnostics. English first; keep message keys localisation-ready but no new translation infrastructure.

Forbidden in normal operator messages: pile, slice, event 2, ledger epoch, tail, lineage, sealed PRIMARY, root, cost authority, generation, replay projection, fact, watermark, rowversion, canonical key, hash, database column/table names, raw SQL/stack traces, NullReferenceException.

### 2.2 Mandatory example: original reported error

Before: `Batch 1: Physical replay would make the stock slice negative at event 2 while replaying.`

Minimum safe after:

```
Cannot roll back Goods Receipt 1.
Removing this receipt would cause insufficient historical stock when a later transaction is applied.
Next step: Review the later stock movements for this item before retrying.
Reference: {correlation ID}
```

Enhanced after, only if directly verified from structured failure context and tenant-scoped history:

```
Cannot roll back Goods Receipt 1 — later stock movement requires the receipt.
Item: {item} — {description}; Warehouse: {warehouse}; Location: {location}; Lot: {lot}.
Affected transaction: {type} {number}, dated {date}.
Stock before that transaction: {before} {UOM}; needed: {required} {UOM}; shortage: {shortage} {UOM}.
Next step: Open the affected transaction or Stock Movement Inquiry and review its dependency before correcting the GRN.
```

Critical precision: If the failure is at the removed receipt itself (while removing) versus a later event (while replaying), give different next steps. If an event refers to a stock-count generation (negative ID) and owner is not verified, do not call it "Material Issue". If item/location or quantities are not determinable, omit the details and retain the safe minimum. For a multi-document action with partial success, say which documents succeeded and which failed, never "No changes were made" globally.

### 2.3 Visible UI contract (reuse existing pages)

- Inventory lists: existing toast/banner plus compact per-selected-batch result details below/on the current confirm area. Example: `Rolled back 2 of 3 receipts; 1 blocked.` Each failure: `Goods Receipt 1 — Later stock movement would cause a shortage. [Why?] [View movements]`.
- Single-document pages: keep input and unsaved selections; show action, reason and next step together; preserve field/line errors in their existing locations. Do not close/edit/reset drafts only to display an error.
- "Why is it blocked?" uses a small expandable details area or existing DxPopup pattern, with good mobile width and accessible focus/labels; no new transaction page.
- If a context lookup fails, still show a truthful friendly failure immediately. No loading spinner indefinitely; optional details may be unavailable.
- Only show Retry after checking both retryability and whether posting outcome is known. Use Refresh status when uncertain; never invoke post/rollback automatically.
- Do not show a programmer-only diagnostic tab to regular users. Existing restricted Costing Center advanced panel is the optional diagnostic destination; ordinary users only see a support reference.

## 3. ARCHITECTURE: ADDITIVE, BOUNDED, PRESENTATION-ONLY

### 3.1 Stable typed presentation contract (suggested; adapt to current naming)

Create an immutable presentation contract in `ErpWeb.Core/Services` or a narrow `ErpWeb.Core/Errors` folder (Core must not depend on Blazor):

```csharp
public sealed record ErpOperatorError(
    string PresentationCode,      // stable UI reason; NOT a new stock business decision
    string Title,                 // Cannot roll back Goods Receipt 1
    string Explanation,           // Verified business reason, no technical internals
    string NextStep,              // User-actionable guidance
    string ReferenceId,
    bool MayRetryAfterRefresh,    // NEVER implies auto-retry or changed posting result
    IReadOnlyList<ErpOperatorErrorDetail> Details,
    IReadOnlyList<ErpRelatedDocumentHint> RelatedDocuments);
```

`ErpOperatorErrorDetail` is safe, pre-formatted, permission-reviewed label/value data; `ErpRelatedDocumentHint` identifies validated doc type/number and a server-verified target, but final navigation still checks rights in UI. Monetary fields must be absent if `ViewCost` is denied. Do not include underlying `Exception`, `Exception.ToString`, `StockPostingId`, raw `EventId`, tenant IDs, SQL fragments or unredacted logs in the user DTO.

Introduce an internal structured failure category in a presenter input such as:

```
Error family: STOCK_SHORTAGE_NOW | HISTORICAL_NEGATIVE_STOCK |
  MISSING_RECEIPT_COST | COST_STATE_NEEDS_REVIEW | UNSUPPORTED_REPLAY |
  DEPENDENT_DOCUMENT | CLOSED_PERIOD | FUTURE_DOCUMENT_DATE |
  UOM_CONVERSION | CURRENCY_RATE | STALE_DOCUMENT |
  ALREADY_PROCESSED | COUNT_LOCKED | PERMISSION_DENIED |
  COST_METHOD_SETUP | RECONCILIATION_REQUIRED | UNKNOWN_FAILURE
Action: POST | ROLLBACK | SAVE | DELETE | RECONCILE ...
Origin: Inventory / Sales / Purchase / Production
Scope: company + branch (from trusted request context, never from untrusted UI)
Target: document type, number, line, affected item if already proven
Context: optional, only when verified
```

Do not use one broad generic map of `INSUFFICIENT_BASE_QTY` → "not enough stock now". The exact same error code can describe a historical replay failure or a current stock-out failure. Use operation + structured reason/producer context. Do not classify a missing valuation as a physical shortage; do not classify an unsupported combination as corruption.

### 3.2 Preserve backend result contracts

- Keep `StockLedgerErrorCodes` constant values unchanged; keep `StockLedgerException` and its original `Error.Message` available for logs and tests. If extra metadata is necessary, add optional init-only fields on the structured error / a companion failure context at the catch boundary. Avoid adding required positional constructor parameters.
- In `IIvInventoryPostingService.cs`, add `ErpOperatorError? OperatorError { get; init; }` (or equivalent) to `IvInventoryPostingBatchResult`, and aggregate errors to `IvInventoryPostingResult` without deleting any batch entry. Existing constructors/factories stay source/binary compatible as applicable.
- Keep `IvInventoryPostingResult.FromBatches` success/error counts, `Batches`, operation IDs and ordering exactly as currently calculated. Its outward `ErrorMessage` may become sanitised operator-friendly text at the response boundary. Preserve raw original only in structured server logs with reference ID; never concatenate raw technical text into UI fallback.
- `IvGoodsReceiptOperationResult.OkPosting` must preserve all fields and pass through the optional details. Other modules' result DTOs should be extended narrowly only if their existing page consumes technical stock/costing failures.
- For existing `ValidationMessageFormat` field/line dictionaries: keep all entries and keys and existing ordered rendering. Do not reclassify a field validation failure as a global costing failure.
- Keep partial success/failure counting and transaction boundaries. Mapping errors must never change a `Succeeded` boolean or result ordering.

### 3.3 Where classification occurs

Preferred flow:

```
EXISTING POST/ROLLBACK BUSINESS SERVICE
    |  unchanged checks/math/locks/commit/rollback
    V
EXISTING failure result / structured StockLedgerException
    |  original error code, exception and diagnostic context preserved
    V
NEW shared operator error presenter (pure deterministic mapping)
    |  optional read-only enrich for proven affected item/document; failure-safe
    V
EXISTING service result with optional OperatorError + safe ErrorMessage
    V
EXISTING UI page: compact actionable message + per-document details
```

Important: Implement mapper at external service/result boundaries; do not change the decision in `StockReplayPhysicalSimulator`, `InventoryValuationService`, FIFO/WAC/Standard projectors, PO settlement or production posting invariants. If a specific event ID is only known at the existing throw site, attaching optional immutable diagnostic context without altering the `if` statement or mathematical inputs is permitted only after tests prove identical behaviour. Otherwise use safe generic wording. Do not parse `ex.Message` to extract the event ID.

### 3.4 Read-only context enrichment (OPTIONAL, NOT A HARD DEPENDENCY)

- Only after the underlying action has failed and its current transaction has completed rollback / exited; don't open another EF context while holding branch stock locks. No writes, no `SaveChanges`, no serialisation/stock locks, no status changes.
- Resolve positive stock event IDs against `IvTrxHistory.Id` plus the trusted `CompanyCode` + `BranchCode`. For negative IDs, use stock-count generation ownership only if exact linkage is proven. Verify active original/reversal status and owner; do not expose unverified/non-current history as the cause.
- Use existing `IvBalLoc` / `IvTrxHistory` / sealed owner / applicable existing cost-trace readers when safe. Never join across another company/branch; never treat item-only or date-only matches as proven document ownership.
- If the original attempt failed under concurrency, data may change before enrichment. If snapshot and live data cannot be shown consistent, omit exact figures/document links; do not claim a different shortage.
- Read maximum one bounded lookup per failed item/event with a defined cap (e.g. 10 batches per inventory request, 20 displayed affected items), no N+1 fan-out on success. Query only requested failures; use indexed columns. Bound time/read cancellation; on timeout/lookup error, log details and use generic message.
- Do not mutate `IvTrxHistory`, `StockPosting`, valuation facts, balances, cost layers or any business entity. Do not persist diagnostic metadata in DB.
- Reuse `CostingSourceNavigationResolver` route semantics in the UI only for proven ownership; don't add a circular Core → UI dependency. Check source module and final target route/authorisation before showing or opening the link.

### 3.5 Security and logs

- Use trusted tenant and branch scope; all context queries include both.
- No cost, exchange amount, variance, pricing, margin or valuation details for users lacking `PermissionCodes.ViewCost` (or the owning module's equivalent). Quantity alone may be visible under ordinary inventory rights as appropriate.
- Use existing `PermissionCodes.Access` for target module before showing a link; never expose a private document number to a user who cannot view that document.
- Generate one correlation/reference ID per failed user action, write structured warning/error log with original code/message and exception stack if applicable; return only reference ID and safe business text to UI. Verify reference can actually be found in existing Serilog logs. Avoid logging passwords, personally identifiable data, or unnecessary customer-sensitive values.
- Unexpected exceptions: generic "We could not complete this action. Check the document status and retry or contact your administrator with reference {id}." Do not display `ex.Message`, SQL/server info, stack or connection string. Do not mislabel uncertain completion as rollback failure.
- Preserve the distinction between Retryable infrastructure conflicts and definitively rejected business operations. "Retry" instruction is an operator hint, never a change to backend retry logic.

## 4. OPERATOR MESSAGE CATALOGUE — REQUIRED FAMILIES

The following is the minimum mapping catalogue, not permission to apply replacements by `string.Replace`. Audit every producer. Add a tested reason-specific mapping and sensible details. A text snippet in parentheses identifies the observed family; final mapping must use structured context, not matching those snippets as business logic.

| # | Trigger / technical source family | Operator headline + precise guidance |
|---|---|---|
| 01 | Physical replay negative during GRN rollback | Cannot roll back this Goods Receipt. A later stock transaction would leave insufficient stock. Review the later movement(s). |
| 02 | Negative during rollback removal itself | Cannot roll back this receipt. The stock balance at its original date would be below zero. Review that date's stock history. |
| 03 | Current stock-out insufficient (IvInventoryPostingService quantity guard) | Not enough stock for {item}. Available {qty}, required {qty}, shortage {qty} {UOM}. Select another eligible stock location or reduce issue quantity. |
| 04 | FIFO issue lacks eligible dated receipt/layer | Cannot issue {item} on {date}. No sufficient costed stock was available on that date. Check earlier receipts and opening stock. |
| 05 | FIFO allocations/layers internally inconsistent | Cannot complete stock costing for {item}. Its cost records need checking. Ask an authorised inventory manager to open Costing Center. |
| 06 | WAC physical vs cost pool mismatch | Cannot complete {action}: stock quantity and cost records for {item} do not agree. Check costing health; do not edit quantities to bypass it. |
| 07 | Standard Cost revision missing | Cannot post {item}. No approved Standard Cost is effective on {date}. Ask the costing administrator to review the item's effective-dated Standard Cost. |
| 08 | Standard Cost revision overlap/conflict | Cannot post {item}. More than one approved Standard Cost applies to {date}. Ask the costing administrator to review the revision dates. |
| 09 | Costing method policy absent | Cannot complete stock posting. No costing method is configured for {date}. Contact the costing administrator. |
| 10 | Policy overlap/invalid method | Cannot complete stock posting. The costing settings are conflicting or incomplete on {date}. Ask the costing administrator to check them. |
| 11 | Costing cutover / old epoch or unsupported historical state | This older transaction cannot be corrected automatically using the current stock costing setup. Ask the inventory manager to review the permitted correction process. |
| 12 | Backdate with later dependencies, replay route unavailable | Cannot post/roll back this earlier transaction automatically. Later stock or costing transactions depend on it. Review affected documents and supported correction options. |
| 13 | GR backdate with both Production issue and invoiced settlement | Cannot change this older Goods Receipt automatically. It affects both Production usage and an already-posted Purchase Invoice. Both dependencies need review. Not "corrupt stock". |
| 14 | Purchase Invoice settlement missing matching GR | Cannot post the Purchase Invoice. A matching posted, costed Goods Receipt was not found for {PO/line/item}. Review GRN and PO linkage. |
| 15 | Purchase Invoice quantity/settlement changed during work | Purchase Invoice information changed while processing. Refresh the document and review the current GRN allocation before retrying. |
| 16 | Purchase CN/DN active settlement exhausted/over quantity | Cannot complete this Purchase Credit/Debit Note. The credited/debited quantity exceeds the remaining eligible invoiced quantity. Review the referenced Purchase Invoice. |
| 17 | FIFO purchase variance cannot be safely allocated | Cannot apply this purchase price adjustment. The remaining receipt cost cannot accept the adjustment. Ask the costing administrator to review the original receipt and purchase adjustment. |
| 18 | Cost valuation missing/unverified (not necessarily stock shortage) | Cannot complete this stock transaction because its cost could not be verified. Review the original receipt or Costing Center. |
| 19 | Missing source/reversal lineage | Cannot reverse this transaction automatically because its original posted stock/cost records cannot be verified. Inventory manager: reconcile the transaction history. |
| 20 | Reversal already exists | This transaction has already been reversed. Refresh and check its latest status; do not submit again. |
| 21 | Document already posted | This document is already posted. Refresh to see the current document status. |
| 22 | Request reused/incomplete transaction outcome | This request may already have been processed. Refresh and verify the document status before taking further action. |
| 23 | Stale stock/document | This document or stock balance changed while you were working. Refresh, review the latest information and try again. |
| 24 | Stock busy/concurrency | Another stock transaction is being processed. Check the current document status, then retry if still needed. |
| 25 | Inventory period closed | Cannot post or roll back a transaction dated {date}. Inventory is closed through {closedThrough}. Ask an authorised period administrator to review the date. |
| 26 | Future dated posting | Cannot post a stock transaction dated {date} yet. You can save it as NEW and post when the business date reaches {date}. (Do not change the rule.) |
| 27 | Stock count freeze | Stock changes are temporarily blocked for this warehouse/location because a Stock Count is in progress. Finish or release the count first. |
| 28 | Stock count snapshot changed | The stock count changed after you opened it. Reload and review the counted quantities. |
| 29 | Stock count unsupported historical shape | Cannot post this backdated Stock Count automatically. This count includes a combination of items/locations not supported for historical recalculation. Review the count and ask an inventory manager. |
| 30 | UOM conversion missing | Cannot calculate the quantity for {item}. An approved conversion from {from} to {to} is missing. Check the item's UOM settings. |
| 31 | Warehouse/location/lot/status missing/inactive | The selected stock location or lot is no longer available. Review the selected warehouse, location, lot and status. |
| 32 | Branch/company context unavailable | Your company or branch selection is not available. Refresh the session or choose the correct branch. Do not display a tenant ID. |
| 33 | Currency exchange rate absent | Cannot calculate the receipt cost. No approved exchange rate for {currency} to {home} covers {date}. Ask the authorised finance administrator. |
| 34 | Opening FIFO cost absent | Cannot cost {item}. A valid opening stock cost or earlier receipt cost is missing. Review opening stock and receipts. |
| 35 | Production Issue to Production negative stock | Cannot issue the material to Production. {item} has insufficient eligible inventory stock. Review source warehouse, lot and requested quantity. |
| 36 | Production issue rollback consumed by output | Cannot roll back this Material Issue. Later Production activity relies on these materials. Review linked Daily Production/Work Order records. |
| 37 | Daily Production output rollback consumed downstream | Cannot roll back this Daily Production document. Its output has been used by a later Production transaction. Review related Work Order movements. |
| 38 | Finished Good Receipt cost/missing cost source | Cannot post the Finished Good Receipt. The Production stock cost is missing or cannot be verified. Review the Work Order's source costs. |
| 39 | Finished Good Receipt rollback with downstream inventory use | Cannot roll back the Finished Good Receipt. Later Inventory or Sales movements depend on the received goods. Review related documents. |
| 40 | Production Stock Return missing original source | Cannot return this material to inventory. The original eligible material issue cannot be verified. Review the Work Order issue history. |
| 41 | Production Stock Adjustment pool mismatch | Cannot adjust this Production stock. The selected Work Order material/WIP balance does not match its recorded movements. Ask the Production inventory manager to review it. |
| 42 | Work Order Transfer balance/cost mismatch | Cannot roll back the Work Order Transfer. Later WIP activity has changed the source or destination balance. Review linked Work Order transfers/movements. |
| 43 | DO/Invoice insufficient shipment stock | Cannot post the delivery/shipment. Available stock for {item} at {warehouse}/{lot} is below the requested shipping quantity. Review shipment quantities/stock source. |
| 44 | DO/Invoice shipment already owned/allocated | Cannot change this shipment line. Its stock has already been allocated/used by another document. Review the linked DO/Invoice. |
| 45 | Sales Invoice COGS source missing/inconsistent | Cannot confirm the stock cost for this invoice. The linked delivery or direct shipment's posted cost needs review. Do not silently post with an invented cost. |
| 46 | Purchase GRN linked to invoice settlement, unsupported rollback | Cannot roll back this Goods Receipt automatically while its Purchase Invoice remains posted. Review related Purchase Invoice and available correction process. |
| 47 | Data integrity/reconciliation mismatch, general fallback | Cannot complete {action}: the related stock or costing records need reconciliation. No technical IDs in UI; give reference and Costing Center (authorised) next step. |
| 48 | Unexpected nonbusiness exception | We couldn't complete {action}. Refresh and check document status. If the issue remains, provide reference {id} to your administrator. |

Coverage of all 21 `StockLedgerErrorCodes`: Build a test matrix for: `LEDGER_DISABLED`, `STOCK_BUSY`, `REQUEST_ID_REUSED`, `DOCUMENT_ALREADY_POSTED`, `STALE_DOCUMENT`, `CLOSED_PERIOD`, `BACKDATED_STOCK_EVENT`, `COUNT_SCOPE_FROZEN`, `INVALID_STOCK_IDENTITY`, `INSUFFICIENT_BASE_QTY`, `INVALID_UOM_CONVERSION`, `REVERSAL_DEPENDENCY`, `REVERSAL_ALREADY_EXISTS`, `LEDGER_MISMATCH`, `LEGACY_LINEAGE_UNVERIFIED`, `HISTORY_BEFORE_CUTOVER`, `VALUATION_REQUIRED`, `COUNT_SNAPSHOT_CHANGED`, `COST_POLICY_MISSING`, `COST_POLICY_INVALID`, `COST_METHOD_NOT_IMPLEMENTED`. These must receive distinct, truthful user defaults; contextual specific variants override generic code defaults.

## 5. PHASED CODING ORDERS AND PASS/FAIL GATES

Rules for agent: Execute Phase 0, report evidence, STOP. Only advance to Phase 1 on an explicit PASSED gate. Apply the same pattern for every phase. Keep commits small and independent. If actual HEAD differs from baseline, report differences first; never silently rewrite concurrent costing work. Every phase report includes exact files changed, why, tests, negative tests, and explicit prohibited-file / business-behaviour checks.

### PHASE 0 — LIVE-HEAD INVENTORY + BASELINE PROOF — READ ONLY

Tasks:

- `git fetch` and inspect actual `productionv2` HEAD/working tree. Record SHA and dirty files; no C# or business-code modifications. Creating the Phase 0 audit/report documents in `plans/` is permitted.
- Find every potentially user-visible stock/costing/quantity error producer and consumer across `ErpWeb.Core`, `ErpWeb.UI`, relevant `ErpWeb.Model.Repositories`, Sales, Purchase, Production, Inventory, StockLedger, Costing, and stock-related inquiry/correction pages. Do not assume the preceding 48 families exhaust the code.
- Search for `StockLedgerException`, `StockLedgerError`, `.Error.Message`, `.ErrorMessage`, `.Message`, `ex.Message`, `throw new`, `Fail(`, `Invalid(`, `Stale(`, `ValidationErrors`, posting/rollback/stock transfer/costing failures, caught exceptions and UI banners.
- Create a markdown or CSV error-site register with: unique ID, path:line, producer method, actual string/code, consumer UI route/action, status (business/user-actionable, configuration, unsupported feature, data integrity, concurrency, internal-only), current UI exposure, proposed reason, verified context, safe fallback, test case. Mark all internal-only messages not shown to UI as INTERNAL_ONLY (no rewrite needed).
- Record baseline tests (existing `ErpWeb.Tests` and integration tests available), baseline method signatures, current `StockLedgerErrorCodes`, per-batch result semantics, baseline generated diff/metrics. Identify coverage gaps and test prerequisites.
- Generate list of explicit no-touch files, inspect if current costing/replay work is concurrently modifying them. Map the six most important external boundaries: Inventory, Sales shipment, Purchase Invoice/CN, Material Issue, Daily Production/FG, Production Stock Return/WOT.
- Compare planned `ErpOperatorError` placement with existing `ValidationMessageFormat`, existing `PageBase` / Sales validation, application DI, and existing alert CSS; avoid duplicate framework creation.

Deliverables: `plans/stock_cost_error_ux_error_site_register.md` (or CSV) + `plans/stock_cost_error_ux_baseline_report.md` committed on target branch only with permission. No C# changes yet.

PASS criteria: Every scope-relevant user-visible error site is classified and linked to its UI consumer, baseline error contract captured, reference tests enumerated. FAIL: Missing any known high-risk page or technical `ex.Message` exposure without disposition.

### PHASE 1 — PURE ERROR PRESENTER + TESTS — NO DB / NO BUSINESS-SERVICE CHANGE

Tasks:

- Add presentation-neutral `ErpOperatorError` / details / related-doc hint types in Core. Add `IStockOperatorErrorPresenter` or similarly scoped pure mapper; may be static if simpler; no EF dependencies.
- Map every existing `StockLedgerErrorCodes` to a safe plain-language fallback, using action and origin to distinguish e.g. negative historical replay versus current availability only if structured reason is supplied.
- Add domain-specific presentation reasons for purchase settlement, production and shipment/stock failures. Do not alter backend status codes, operation result logic, throwing conditions or error types.
- Ensure message text is deterministic, no raw `Exception.ToString`, no diagnostic IDs; optional details formatted safely. Empty/missing contextual fields produce truthful generic strings, not placeholder `{item}`.
- Build unit tests for all 21 stable ledger codes, all catalogue families above, duplicates, nulls, date/UOM formatting, technical-word leakage and very long/malicious input (encoding handled by Razor, no `MarkupString` for untrusted text).
- Keep existing `ValidationMessageFormat` tests and return values unchanged; use it instead of creating a second field-validation engine.

PASS: Pure mapper tests green, compilation succeeds, not a single existing posting/quantity/costing source file modified, no data access, no user behaviour change yet. FAIL: Generic classification changes the meaning of an exception, or the mapper leaks technical text.

### PHASE 2 — STRUCTURED RESULT PROPAGATION + ORIGINAL GRN FIX

Tasks:

- Add optional `OperatorError` to `IvInventoryPostingBatchResult` and aggregate result, and preserve `StockLedgerError.Code` / `Retryable` across the existing `StockLedgerException` catch in `IvInventoryPostingService.DispatchAsync`. Keep all `Succeeded`, `Batches`, `OperationId`, totals and the original internal error available in logs.
- `IvGoodsReceiptOperationResult.OkPosting` carries optional per-batch user errors through unchanged success/partial-success semantics.
- In `IvGoodsReceiptList.razor.cs` and its `.razor`, replace technical toast with friendly per-batch summary and minimal expandable detail; preserve busy state, confirm result, grid reload behaviour and selection semantics. No new page.
- Add only narrow optional metadata at an error producer if genuinely required for accurate classification. `StockReplayPhysicalSimulator.EnsureNonnegative` is eligible for metadata only (event + removing/replaying distinction). Leave its `if (quantity < 0m)`, rounding, exception code, exception text and call site sequence untouched. Better to omit details than risk calculation changes.
- Do not claim the later transaction is Batch 2, and do not infer material issue/lot from `event 2` by string parsing.
- Add focused unit/integration test: GRN Batch 1, later issue, current total positive but intermediate historical balance negative. Assert rollback still fails, same code, same stock/cost/database state, and ordinary user sees reason + next action with no `pile`/`slice`/`event`/`replaying`.
- Preserve partial rollback success status across 1, 2 and 10 selected batches, and no double posting from retries.

PASS: Original reported GRN message is fully operator-friendly; failure still occurs; per-batch counts and DB effects exactly match baseline. FAIL: Changed any replay algorithm/quantity values, or raw technical text still shows in GRN UI.

### PHASE 3 — READ-ONLY AFFECTED DOCUMENT EXPLANATION

Tasks:

- Add small bounded, tenant-scoped read-only explanation reader at service boundary, if Phase 0 evidence confirms safe lineage path. Use trusted `CompanyCode`+`BranchCode`, `IvTrxHistory` / exact stock-count generation if relevant and existing owner-lookup patterns. Use `AsNoTracking` and bounded results; do not query within active stock transaction lock.
- Only enrich after a definitive failed operation; no queries on success. Validate owner and exact stock identity; where uncertain, omit linked record and amounts.
- Optionally derive affected item/warehouse/lot/date and shortage from deterministic failure metadata if supplied, not from current on-hand alone. DO NOT recompute the costing algorithm for error explanation.
- Reuse existing UI source navigation resolver; check `Access` and `ViewCost` permissions, block cross-tenant records; ensure unsupported or missing route has no broken button.
- Add tests for positive history event ID, negative stock-count generation, reversed/obsolete movement, missing history row, different tenant, denied access, stale evidence, failing lookup, timeout, and multi-batch query cap.

PASS: Related-document explanation is accurate when proven; safe generic message when not; no additional writes, no cross-tenant leakage, no modification to rollback outcome. FAIL: Context lookup fails the original action a second time, introduces false details, exceeds bounds or changes posting speed/locks materially.

### PHASE 4 — REMAINING INVENTORY / STOCKLEDGER / COSTING UI

Scope (existing pages only): GRN entry/list, MR, MI, Scrap, Vendor Return, Customer Return, Stock Transfer, Stock Adjustment, Stock Count, Inventory Inquiry/Costing Center, and any additional Inventory stock posting/rollback UI discovered in Phase 0.

Tasks:

- Carry structured safe errors at each existing public operation boundary; avoid broad rewrites of internal calculators/planners.
- Cover FIFO, WAC, Standard, shortage, UOM, close period, future-date posting, stock count, backdated replay unsupported, configuration and reconciliation failure families.
- Preserve every field/line validation, per-document batch result, operation flags, modal actions, status/selection state. Display errors in existing page styles.
- Keep money/valuation details `ViewCost`-gated; only suggest actual available menu actions.
- Test POST, ROLLBACK, valid vs invalid dates, count freeze, same-day history, FIFO consumed receipt, WAC missing cost, Standard revision missing, and mixed batch results.

PASS: All Phase-0 Inventory/StockLedger UI-facing error sites mapped/presented or documented as intentionally safe; no raw technical errors or introduced behaviour changes. FAIL: Missing an Inventory UI path or removed an original validation.

### PHASE 5 — CROSS-MODULE STOCK / COST ERRORS

Scope: Sales DO and direct Invoice stock posting/shipment, Purchase Invoice/CN/DN/GRN costing dependencies, Production Material Issue, Daily Production, FG Receipt, Production Stock Return, Production Stock Adjustment, Work Order Transfer, and stock-sensitive views of those actions.

Tasks:

- Extend the same operator contract or compatible safe message boundary to the existing service result types only where scoped errors reach UI. Preserve existing `SaDoErrorKind`, `SaInvoiceErrorKind`, validation dictionaries, purchase/production result contracts and operation statuses.
- Replace scoped UI `ex.Message` assignment with safe operator handling + logging reference. Unexpected errors must not be misrepresented as current stock shortage.
- Ensure POST vs ROLLBACK and unsupported replay vs corrupt evidence have different messages and next actions. On linked PI/GRN/Production issues, cite exact documents only if verified.
- Preserve the Sales UI workflow changes already in progress; keep direct DO/Invoice ability unchanged. Avoid moving shipment validation or changing Delivery Order/Invoice business rules.
- No new transaction pages, no new approval flows, no SQL migrations, no costing/stock recalculation changes.
- Run module-specific result, permission, status, backdated and stock/valuation regression tests, including direct Invoice and PO/GRN/PI dependencies.

PASS: Every Phase-0 cross-module stock/cost error site has safe user presentation; no production/stock/cost calculation diff or status decision changes. FAIL: Changed a Sales/Procurement workflow to improve its message or converted an unsupported feature into a silent success.

### PHASE 6 — REPOSITORY-WIDE SAFETY VERIFICATION AND UAT

Tasks:

- Re-run automated error-site census on current HEAD, compare with Phase 0 register, identify all unmapped user-visible stock/cost/quantity failures and resolve them. Keep INTERNAL_ONLY technical exceptions for diagnostics.
- Run build and available unit/integration suites. Required commands where repository supports them: `dotnet restore ErpWeb.slnx`, `dotnet build ErpWeb.slnx -c Release --no-restore`, `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release` (adapt to actual solution SDK and test setup; report actual executed commands and results, never fabricate).
- Snapshot/test before vs after: same batches and transaction dates, same `IvBalLoc.StdQty`, `IvTrxHistory` active+reversal history, stock posting roots, `StockValuationFact`, FIFO layers/consumption, WAC/Standard state, Purchase Invoice settlement, Production balances/WIP, DO/Invoice shipment status, closed period, concurrency, idempotency. Compare only same initial state and well-defined outcomes.
- Add a guardrail diff audit: list all changed files, show every changed code block in posting/costing areas (ideally none except allowable diagnostic metadata/error boundary), explicitly prove no changed `if` logic or arithmetic; inspect SQL/EF writes, transaction/lock boundaries and status assignment. Diff with baseline and concurrent changes accounted for.
- Test normal user without `ViewCost`, inventory admin with `ViewCost`, Sales user without Procurement access, different company/branch, document link permission, unknown history, malformed error, multiple failures.
- Real UAT script for warehouse and purchasing staff: failed GRN rollback, FIFO shortage, Purchase Invoice cost settlement, Production Material Issue, FG rollback, DO/Invoice shipment, WIP transfer. They must explain the error without `event 2`, `epoch`, `tail`, `lineage`, etc.
- Confirm no runtime error page displays an exception stack or raw SQL to ordinary users; verify related server log is findable by reference ID.
- For any test that requires production-like data and cannot be executed, mark NOT RUN and give a reproducible test script; never claim full pass.

PASS: Zero unreviewed scoped user-visible error sites, zero raw technical exceptions on ordinary scoped transaction UI, no verified costing/quantity/posting behaviour drift, and all required tests green or accurately recorded as outstanding blockers. FAIL: Any case succeeds/fails differently, changes balances or costs, hides business validation, leaks tenant/cost data, or fabricates error context.

### PHASE 7 — FINAL RELEASE PACKAGE / HANDOFF (ONLY AFTER PHASE 6 PASS)

Deliver:

- `plans/stock_cost_error_ux_error_site_register.md` (final, all dispositions).
- `plans/stock_cost_error_ux_implementation_report.md`: actual SHA(s), changed files, per-phase tests, known issues and security/permission proof.
- `plans/stock_cost_error_ux_operator_message_catalogue.md`: final codes, operator messages, next actions, fallback priority, locale keys, owner module and link policy.
- `plans/stock_cost_error_ux_business_parity_matrix.md`: PASS/FAIL/NOT RUN for each critical stock/cost business invariant.
- Demo screenshots or textual UAT evidence from existing screens for GRN Batch 1, partial rollback, FIFO/WAC/Standard, Sales, Purchase, Production.
- Separate list of unrelated pre-existing backend defects discovered: report only, do not fix them in this change.

## 6. SAFETY ACCEPTANCE MATRIX — ALL REQUIRED

| Area | Before/after assertion | Gate |
|---|---|---|
| `INSUFFICIENT_BASE_QTY` historic negative | The exact same rejected GRN rollback stays rejected; status unchanged and original ledger error code unchanged. | Critical |
| Original event history | No extra/missing/changed `IvTrxHistory` rows because of an error explanation. | Critical |
| Physical balance | Same `IvBalLoc.StdQty` per item/warehouse/location/lot/status and same `RowVersion` write behaviour for equivalent operation. | Critical |
| FIFO | Same cost layers, balances, allocation, receipt/issue valuation and historical replay. | Critical |
| WAC | Same moving average and quantities, values and cost corrections; no changes to replay. | Critical |
| Standard | Same cost revision/date selection, amount and variance. | Critical |
| Stock counts | Same count resets/adjustments, existing guard/freeze, month-end rules and historical count behaviour. | Critical |
| Procurement | Same GRN-PO quantity, PI settlement, CN/DN adjustment and reversal. | Critical |
| Sales | Same DO/Invoice shipment, reservation, posting status, direct-document flows, COGS. | Critical |
| Production | Same IP, Daily Production, FG, return, adjustment, WO transfer and WIP calculations. | Critical |
| Concurrency | Same lock/isolation, retry/idempotency decisions; no new races caused by error enrichment. | Critical |
| Multi-batch | Identical `SucceededCount`, `FailedCount`, per-batch success flags and ordering; meaningful per-batch error UI. | Critical |
| Permissions | Cost amounts masked without `ViewCost`; no unpermitted links or cross-branch history details. | Critical |
| Friendly UX | What, why, affected document, next action understandable without looking at source. | High |
| Fallback | Missing/enrichment-error/stale-context yields safe truthful error, never blank, never raw exception. | High |
| Logging | Original technical code/message and correlation ID remain available to authorised support. | High |
| Existing field validation | All keys/rows and messages remain available (use `ValidationMessageFormat`); nothing silently dropped. | Critical |
| Performance | No context queries on success; bounded reads on errors, no new writes, no long-running lock-held lookups. | High |
| Scope | No schema changes, no new transaction screen, no change to costing/posting/rollback rules. | Critical |

## 7. STRICT FAILURE MODES AND ANTI-PATTERNS

Stop and flag as NON-COMPLIANT if agent does any of these:

- "Fixes" the rollback by allowing intermediate negative historical quantity or removing `EnsureNonnegative`.
- Changes FIFO layers, WAC calculation or posting dates because of a confusing message.
- Replaces the server's `StockLedgerError.Code` with a presentation code, or changes `Retryable` to make the UX easier.
- Maps every `LEDGER_MISMATCH` to "insufficient stock", every `INSUFFICIENT_BASE_QTY` to "no stock now", or every unsupported replay to "database corrupt".
- Looks up `IvTrxHistory.ID=2` without company/branch filter or calls it "Batch 2" / "MI 2" without proving its owner.
- Parses free-text error descriptions to identify IDs, costs, item codes or affected docs.
- Sends raw error messages to default UI or silently suppresses a precise field validation.
- Changes a page to automatically retry `PostAsync` or `RollbackAsync` following an uncertain exception.
- Says "No stock or costing changes were made" across mixed-success batch operations.
- Uses the cost trace for a user who cannot `ViewCost`, links to unauthorised module pages, or navigates using user-supplied URL values.
- Performs DB joins/read-enrichment while the posting/rollback transaction or stock branch lock remains active.
- Changes current costing logic and claims it was a "small refactor".
- Claims all user-visible sites are covered without a complete, reviewed Phase 0 → Phase 6 error-site register.

## 8. REQUIRED AGENT REPORT FORMAT AFTER EACH PHASE

```
PHASE: N — <NAME>
BRANCH / BASE SHA / RESULT SHA:
GATE: PASSED | FAILED | BLOCKED | NOT RUN
FILES CHANGED: [exact paths]
SCOPE COMPLIANCE: [YES/NO; no core math/quantity/posting rules changed]
ERROR-SITE COVERAGE: [inspected / converted / internal-only / remaining]
MESSAGE SAMPLES: [before → after for each changed family]
TESTS: [exact commands, totals, PASS/FAIL/NOT RUN]
BUSINESS PARITY: [inventory qty, FIFO, WAC, Standard, PI, Sales, Production]
SECURITY: [tenant / ViewCost / linked-doc access]
REGRESSIONS OR UNSOLVED ISSUES:
NEXT PHASE: [do not begin before gate review]
```

Mandatory pause rule: On any gate failure, stop and write a focused fix sub-plan; do not change costing or balance logic to make the test pass. If a pre-existing bug is found, report it as separate work, with evidence.

## 9. COPY/PASTE PROMPT FOR CURSOR / CODEX / AI CODING AGENT

Implement ONLY Phase 0 of `plans/productionv2_ERP_FRIENDLY_STOCK_COSTING_ERROR_MESSAGES_STRICT_AI_CODING_AGENT_PLAN_v1.md` on the `productionv2` branch. If the plan is a downloaded file, first place this exact file under `plans/` without editing existing business code. First verify live HEAD and inspect all relevant actual source files. Produce a complete user-visible stock/cost/quantity error-site register and baseline parity report. Do not modify business logic or C# code during Phase 0. Report with exact file paths and evidence, then STOP and wait for Phase 0 gate approval. Do not touch FIFO, WAC, Standard, stock quantity balance, posting/rollback, replay, PO settlement, WIP, database schema or transaction screens. On subsequent phases follow the same phase-gate discipline and preserve identical business decisions and database effects. Never silently change a business control to make an operator message clearer.

To continue after gate approval:

Phase N gate: PASSED. Implement ONLY Phase N+1 of the same plan, verify current HEAD, stay inside allowed message/result/UI scope, run the specified tests, report exact diffs, and STOP.

## 10. REVIEW / APPROVAL CRITERIA

| Dimension | Weight | Required |
|---|---|---|
| Stock/costing/posting/rollback behaviour preserved | 35% | No exceptions; critical gate |
| Error accuracy and actionable operator guidance | 25% | No guessed cause/document/quantities; clear next step |
| End-to-end coverage, scope and per-batch handling | 15% | Error-site register complete; no raw scoped exceptions |
| Security, tenant isolation and cost permission | 10% | No unauthorised data/links |
| Tests and concurrency/partial-success parity | 10% | Evidence-backed |
| Minimal maintainable design | 5% | Reuse existing contracts/patterns |

Approval threshold: ≥ 9.6/10, with all critical acceptance conditions passing. This is a target threshold, not a claim that implementation has been tested. Agent must report actual evidence; if integration/production-like tests are unavailable, mark them NOT RUN and do not claim a 10/10 release gate.

Final intended result: Exactly the same valid and invalid ERP transactions as today, exactly the same stock and costing numbers, but understandable error messages with actionable, permission-safe detail across StockLedger, Inventory, Procurement, Sales and Production.
