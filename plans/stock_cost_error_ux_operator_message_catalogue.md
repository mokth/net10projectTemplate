# Operator message catalogue

**Scope:** every stock / costing / quantity failure surfaced by Inventory, Procurement, Sales and Production
posting screens.
**Source of truth:** `ErpWeb.Core/Services/ErpOperatorErrorPresenter.cs` (classification, wording, detail
budget), `ErpWeb.Core/Services/ErpOperatorErrors.cs` (cross-module factories),
`ErpWeb.Core/Inventory/IvInventoryOperatorErrors.cs` (inventory transaction-type mapping),
`ErpWeb.UI/Components/Common/OperatorError/` (rendering).
**Rendering:** `ErpOperatorFailureView` → `<ErpOperatorFailureBanner>` (title, reason, next step,
"why" facts, related documents, reference).

> Localization status: the solution has **no** `IStringLocalizer`/`AddLocalization`/`.resx` infrastructure.
> The `Locale key` column is a **reserved key**, not a wired resource. Adding a resource provider later
> only needs the presenter to resolve `key` → text; no classification logic changes.

---

## 1. Locale key convention

```
erp.err.<origin>.<family>[.<phase>].title
erp.err.<origin>.<family>[.<phase>].why
erp.err.<origin>.<family>[.<phase>].next
```

`<origin>` and `<family>` are the lowercase forms of the presentation code (`INVENTORY.HISTORICAL_NEGATIVE_STOCK_REMOVING`
→ `erp.err.inventory.historical_negative_stock.removing.title`). Because the presentation code is already
stable and tested (§3), keys can be generated mechanically.

---

## 2. Code → family mapping (all 21 persisted codes)

`ErpOperatorErrorPresenter.ResolveFamily` — structured family first, then code default. Text is never parsed.

| # | `StockLedgerErrorCodes` | Value | Family | Presentation code | Owner module | Notes |
|---|---|---|---|---|---|---|
| 1 | `LedgerDisabled` | `LEDGER_DISABLED` | `UnsupportedReplay` | `STOCK.UNSUPPORTED_REPLAY` | StockLedger | Ledger switched off for the branch/company |
| 2 | `StockBusy` | `STOCK_BUSY` | `StockBusy` | `STOCK.STOCK_BUSY` | StockLedger | Only family that may say "try again" after refresh |
| 3 | `RequestIdReused` | `REQUEST_ID_REUSED` | `AlreadyProcessed` | `STOCK.ALREADY_PROCESSED` | StockLedger | Distinct wording: "may already have been processed" |
| 4 | `DocumentAlreadyPosted` | `DOCUMENT_ALREADY_POSTED` | `AlreadyProcessed` | `STOCK.ALREADY_PROCESSED` | StockLedger | "already posted" |
| 5 | `StaleDocument` | `STALE_DOCUMENT` | `StaleDocument` | `STOCK.STALE_DOCUMENT` | StockLedger | Refresh + retry allowed |
| 6 | `ClosedPeriod` | `CLOSED_PERIOD` | `ClosedPeriod` | `STOCK.CLOSED_PERIOD` | Inventory | Shows `dd/MM/yyyy` document date and closed-through date |
| 7 | `BackdatedStockEvent` | `BACKDATED_STOCK_EVENT` | `UnsupportedReplay` | `STOCK.UNSUPPORTED_REPLAY` | StockLedger | |
| 8 | `CountScopeFrozen` | `COUNT_SCOPE_FROZEN` | `CountLocked` | `STOCK.COUNT_LOCKED` | Inventory | |
| 9 | `InvalidStockIdentity` | `INVALID_STOCK_IDENTITY` | `StockIdentityUnverified` | `STOCK.STOCK_IDENTITY_UNVERIFIED` | Inventory | Never prints tenant/company/branch ids |
| 10 | `InsufficientBaseQty` | `INSUFFICIENT_BASE_QTY` | `HistoricalNegativeStock` **or** `StockShortageNow` | `STOCK.HISTORICAL_NEGATIVE_STOCK[_REMOVING\|_REWINDING\|_REPLAYING]` / `STOCK.STOCK_SHORTAGE_NOW` | Inventory / Sales / Purchase / Production | The only dual-meaning code. Historical when action is rollback/cancel **or** a replay phase was supplied |
| 11 | `InvalidUomConversion` | `INVALID_UOM_CONVERSION` | `UomConversion` | `STOCK.UOM_CONVERSION` | Inventory | Shows `from`/`to` when supplied |
| 12 | `ReversalDependency` | `REVERSAL_DEPENDENCY` | `DependentDocument` | `STOCK.DEPENDENT_DOCUMENT` | Inventory | |
| 13 | `ReversalAlreadyExists` | `REVERSAL_ALREADY_EXISTS` | `AlreadyProcessed` | `STOCK.ALREADY_PROCESSED` | Inventory | "already been reversed" |
| 14 | `LedgerMismatch` | `LEDGER_MISMATCH` | `ReconciliationRequired` | `STOCK.RECONCILIATION_REQUIRED` | StockLedger | Costing Centre next step, never "corrupt stock" |
| 15 | `LegacyLineageUnverified` | `LEGACY_LINEAGE_UNVERIFIED` | `ReconciliationRequired` | `STOCK.RECONCILIATION_REQUIRED` | StockLedger | |
| 16 | `HistoryBeforeCutover` | `HISTORY_BEFORE_CUTOVER` | `UnsupportedReplay` | `STOCK.UNSUPPORTED_REPLAY` | StockLedger | |
| 17 | `ValuationRequired` | `VALUATION_REQUIRED` | `MissingReceiptCost` | `STOCK.MISSING_RECEIPT_COST` | StockLedger | Customer-approved wording ("keep") |
| 18 | `CountSnapshotChanged` | `COUNT_SNAPSHOT_CHANGED` | `StaleDocument` | `STOCK.STALE_DOCUMENT` | Inventory | |
| 19 | `CostPolicyMissing` | `COST_POLICY_MISSING` | `CostMethodSetup` | `STOCK.COST_METHOD_SETUP` | StockLedger | |
| 20 | `CostPolicyInvalid` | `COST_POLICY_INVALID` | `CostMethodSetup` | `STOCK.COST_METHOD_SETUP` | StockLedger | "conflicting or incomplete" variant |
| 21 | `CostMethodNotImplemented` | `COST_METHOD_NOT_IMPLEMENTED` | `CostMethodSetup` | `STOCK.COST_METHOD_SETUP` | StockLedger | |

Some families are reachable only via a structured factory (no persisted code exists for them):

| Family | Producer | Presentation code |
|---|---|---|
| `MissingReceiptCost` (FG/production variant) | `ProductionOperatorErrorLog.Ledger` | `PRODUCTION.MISSING_RECEIPT_COST` |
| `CostStateNeedsReview` | Production WIP/pool producers | `PRODUCTION.COST_STATE_NEEDS_REVIEW` |
| `ReconciliationRequired` (no ledger code) | `ErpOperatorErrors.ReconciliationRequired` | `<ORIGIN>.RECONCILIATION_REQUIRED` |
| `StaleDocument` (concurrency, no ledger code) | `ErpOperatorErrors.Stale` | `<ORIGIN>.STALE_DOCUMENT` |
| `UnknownFailure` | `ErpOperatorErrors.Unexpected`, `ProductionOperatorErrorLog.Unexpected` | `<ORIGIN>.UNKNOWN_FAILURE` |
| `PermissionDenied` | reserved (no producer yet) | `<ORIGIN>.PERMISSION_DENIED` |

---

## 3. Family → operator text (verbatim templates)

`{verb}` = post / roll back / cancel / save / delete / complete / reconcile.
`{target}` = `this goods receipt`, `Goods Receipt IV-GR-000123`, `this record`, `this action`.
Placeholders in `{}` are already-substituted values; an empty context is asserted to contain no `{`, `}`
or `null` (`ErpOperatorErrorPresenterTests.Empty_context_never_renders_a_placeholder`), and every family
is asserted against a forbidden-vocabulary regex (slices, piles, epoch, lineage, rowversion, tail, event,
hash, SQL, FIFO, WAC, ledger, replay, stack trace, posting id, history id, tenant id).

| Family | Title | Reason ("why") | Next action | Retry after refresh |
|---|---|---|---|---|
| `HistoricalNegativeStock` (phase `Removing`) | `Cannot {verb} {target}.` | Removing this record would make the stock balance at its original date fall below zero. | Review the stock history for that date before retrying. | no |
| `HistoricalNegativeStock` (phase `Rewinding`) | `Cannot {verb} {target}.` | A later stock movement for this item cannot be reversed against the current stock balance. | Review the later stock movements for this item. | no |
| `HistoricalNegativeStock` (phase `Replaying`/none) | `Cannot {verb} {target}.` | Removing this record would cause insufficient historical stock when a later transaction is applied *(`dated {date}` when supplied)*. | Review the later stock movements for this item before retrying. | no |
| `StockShortageNow` | `Cannot {verb} {target}.` | There is not enough available stock for `{item}`. | Select another eligible stock location or reduce the quantity, then try again. | no |
| `StockShortageNow` (Production origin, post/save) | `Cannot post {target}.` | There is not enough eligible stock to issue `{item}` to Production. | Select another eligible stock location or reduce the quantity, then try again. | no |
| `MissingReceiptCost` | `Cannot {verb} {target}.` | The cost of this stock movement could not be verified `{item}`. | Review the original receipt, or ask an authorised costing user to check Costing Center. | no |
| `CostStateNeedsReview` | `Cannot {verb} {target}.` | The stock quantity and the cost records do not agree `{item}`. | Ask an authorised costing user to check the costing health. Do not adjust quantities to bypass it. | no |
| `UnsupportedReplay` | `Cannot {verb} {target}.` | This older transaction cannot be corrected automatically with the current stock costing setup. | Ask the inventory manager to review the permitted correction process. | no |
| `DependentDocument` | `Cannot {verb} {target}.` | A later stock or costing transaction depends on this record. *(rollback/cancel)* / A related stock or costing transaction depends on this record. *(other)* | Review the affected document and the supported correction options. | no |
| `ClosedPeriod` | `Cannot {verb} {target}.` | A transaction dated `{dd/MM/yyyy}` falls in a closed inventory period (closed through `{dd/MM/yyyy}`). | Ask an authorised period administrator to review the date. | no |
| `FutureDocumentDate` | `Cannot {verb} {target}.` | A stock transaction dated `{dd/MM/yyyy}` cannot be posted yet. | Save it as NEW and post it when the business date reaches that date. | no |
| `UomConversion` | `Cannot {verb} {target}.` | No approved conversion from `{from}` to `{to}` is available `{item}`. | Check the item's unit conversion settings. | no |
| `CurrencyRate` | `Cannot {verb} {target}.` | No approved exchange rate from `{currency}` to `{home}` covers `{dd/MM/yyyy}`. | Ask the authorised finance administrator to enter the exchange rate. | no |
| `StaleDocument` | `Cannot {verb} {target}.` | This record or its stock balance changed while you were working. | Refresh, review the latest information and try again. | **yes** |
| `AlreadyProcessed` (`DOCUMENT_ALREADY_POSTED`) | `Cannot {verb} {target}.` | This document is already posted. | Refresh to see the current status. Do not submit again. | no |
| `AlreadyProcessed` (`REVERSAL_ALREADY_EXISTS`) | `Cannot {verb} {target}.` | This transaction has already been reversed. | Refresh to see the current status. Do not submit again. | no |
| `AlreadyProcessed` (`REQUEST_ID_REUSED`) | `Cannot {verb} {target}.` | This request may already have been processed. | Refresh to see the current status. Do not submit again. | no |
| `CountLocked` | `Cannot {verb} {target}.` | Stock changes are temporarily blocked for this warehouse or location because a stock count is in progress. | Finish or release the stock count first. | no |
| `PermissionDenied` | `Cannot {verb} {target}.` | You do not have permission to perform this action. | Ask an authorised user to review your access rights. | no |
| `CostMethodSetup` | `Cannot {verb} {target}.` | No valid costing method is configured for `{dd/MM/yyyy}`. / The costing settings are conflicting or incomplete for `{dd/MM/yyyy}`. | Ask the costing administrator to check the costing settings for that date. | no |
| `ReconciliationRequired` | `Cannot {verb} {target}.` | The related stock or costing records need reconciliation. | Give the reference to your administrator, or ask an authorised inventory manager to check Costing Center. | no |
| `StockBusy` | `Cannot {verb} {target}.` | Another stock transaction is being processed for this branch. | Check the current document status, then try again if it is still needed. | **yes** |
| `StockIdentityUnverified` | `Cannot {verb} {target}.` | The stock record, or your company and branch selection, could not be verified. | Refresh the session, or check the selected warehouse, location, lot and status. | no |
| `UnknownFailure` | `Cannot {verb} {target}.` | The action could not be completed. | Refresh and check the document status. If the problem continues, give the reference to your administrator. | no |

Verbs by action: `Post`→post, `Rollback`→roll back, `Cancel`→cancel, `Save`→save, `Delete`→delete,
`StockCount`→complete, `Reconcile`→reconcile, otherwise `complete`.

---

## 4. Fallback priority

Applied once, in this fixed order (first match wins; later steps never override an earlier one):

1. **Structured operator error already attached by the producer** (`result.OperatorError`,
   `IvInventoryOperatorErrors`, `ErpOperatorErrors.*`, `ProductionOperatorErrorLog.*`) → render it.
2. **Structured ledger refusal without a pre-built view** → `ErpOperatorErrors.FromLedgerError`
   using the persisted `StockLedgerError.Code` (never the message text).
3. **Known non-ledger failure with a typed factory** (`ReconciliationRequired`, `Stale`, `Unexpected`)
   → render that family.
4. **No structured error** → render the page's existing message unchanged (`FromError`/`FromRecords`
   return `null`). No invented explanation.
5. **No message at all** → generic `UnknownFailure` text with whatever reference exists. Never blank,
   never an exception string, never a stack trace.

Rules that hold at every step:
- At most **12** detail rows; producer values truncated at **80** characters.
- Monetary details are dropped unless `CanViewCost`; a `null` cost detail is never replaced by `0`.
- At most **5** related-document links; a link needs both a document type and a number.
- Dates are `dd/MM/yyyy`; ISO dates, GUIDs, tenant ids, `StockLedgerErrorCodes` values, event ids,
  `epoch`, `slice`, `tail`, `lineage`, `pile` and `rowversion` never reach the operator.
- Technical code and original message stay in the server log with the same reference printed in the banner.

---

## 5. Presentation codes actually produced

Format `<ORIGIN>.<FAMILY>[_<PHASE>]`, origin from the producer:

```
INVENTORY.*   SALES.*   PURCHASE.*   PRODUCTION.*   STOCK.* (origin not supplied)
```

Examples in use today:

```
INVENTORY.STOCK_SHORTAGE_NOW
INVENTORY.HISTORICAL_NEGATIVE_STOCK_REMOVING
INVENTORY.HISTORICAL_NEGATIVE_STOCK_REWINDING
INVENTORY.CLOSED_PERIOD
INVENTORY.STALE_DOCUMENT
SALES.STOCK_SHORTAGE_NOW
SALES.DEPENDENT_DOCUMENT
PURCHASE.MISSING_RECEIPT_COST
PURCHASE.RECONCILIATION_REQUIRED
PRODUCTION.STOCK_SHORTAGE_NOW
PRODUCTION.HISTORICAL_NEGATIVE_STOCK
PRODUCTION.COST_STATE_NEEDS_REVIEW
PRODUCTION.RECONCILIATION_REQUIRED
PRODUCTION.UNKNOWN_FAILURE
```

The code is stable and intended for support macros and future localization; it is **not** shown to
operators as a headline (the headline is the sentence).

---

## 6. Owner module per message family

| Origin | Owning services | Log writer |
|---|---|---|
| Inventory | `IvInventoryPostingService`, `IvGoodsReceiptService`, `IvStockCountService`, `IvMasterResults` consumers | `IvInventoryOperatorErrors` + `ILogger<…>` |
| Sales | `SaDoService`, `SaInvoiceService`, `SaCdnService` | `ErpOperatorErrors.FromLedgerError` + `ILogger<…>` |
| Purchase | `PoInvoiceService`, `PoCdnService` | `ErpOperatorErrors.FromLedgerError` + `ILogger<…>` |
| Production | `ProductionMaterialIssueService`, `ProductionOutputService`, `ProductionFinishedGoodReceiptService`, `ProductionStockReturnService`, `ProductionStockAdjustmentService`, `ProductionWorkOrderTransferService` | `ProductionOperatorErrorLog` |
| StockLedger | any producer of `StockLedgerError` | caller's logger |

Escalation owner shown to the operator: **costing administrator** (cost method / cost verification),
**inventory manager** (historical correction, reconciliation), **period administrator** (closed period),
**finance administrator** (exchange rate). Never "the developer", never "IT support" for a business refusal.

---

## 7. Link policy

| Link type | Shown | Condition |
|---|---|---|
| The refused document | only as a **name** in the headline (`Goods Receipt IV-GR-000123`), not a hyperlink | document kind is a known `ErpDocumentKind` **and** number is ≤24 chars |
| Affected/later transaction | as a detail row (`Affected transaction`, `Affected transaction date`) | producer supplied both a known kind and a number |
| Related documents | up to 5 clickable hints | type + number supplied; route only used when supplied by the producer |
| Costing Centre | textual next step, **no link** | only for costing/reconciliation families |
| Any other module's document | **never** | prevents a Sales user being offered a Purchase document they cannot open |
| Tenant / company / branch identifier | **never** in a link or a detail | |
| Unknown document kind | no name invented; falls back to "this record" | `DocumentLabel` returns `null` |

Monetary values are never rendered as links, and are dropped entirely without `ViewCost`.
