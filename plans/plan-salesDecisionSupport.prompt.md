# Plan: Sales Decision-Support Screens

> **Status:** **REV 2 (2026-09-26) — revised after external review.** The review (9.2/10) is dispositioned item-by-item in §0.1 and preserved verbatim in **Appendix A**. **Blocking change:** the semantic-verification gate (§12, Gate 1–3) must pass before any Phase B rule is coded. Sign-off is still wanted on **§11.1** (business/product decisions); **§11.2** is *verification against the shipped code*, not preference.
> **Prerequisite plan:** `plans/plan-salesReportsAndInquiries.prompt.md` (Phases 1–3 shipped; Phase 4 AR/COGS gated).
> **Owner-visible outcome:** the Sales "Inquiry" group stays as raw detail; a new **Sales Monitor** group gives managers/finance/sales-ops the *decision, debug and analysis* views the current listings lack.

## 0. Change log

| Rev | Date | Change |
|---|---|---|
| 1 | 2026-09-26 | Initial plan — `SA_MONITOR`, 8 screens, D1–D9. |
| 2 | 2026-09-26 | External review incorporated (§0.1). Added the **quantity/status semantics gate** (§12) and a verified-facts register (§11.0). Renamed A2 → **Delivered Not Fully Invoiced** (per-line no-invoice / partial / full / written-off split), C1 → **Customer Sales Analytics**, C3 → **Sales Tax Analysis**. Fixed the A1 ageing vocabulary, the two QT expiry rules, the B1 severity contract (`Error` requires a documented engine invariant), the growth-when-prior-is-zero rule, and the credit-proxy label. Charts demoted from an architectural rule to a UX guideline. Authorization test matrix specified (§13.5). |
| 3 | 2026-09-26 | **Gate 1 executed (read-only) and Gate 2 applied — see §0.2.** Phase A (A1–A4) **IMPLEMENTED**. Two DTO fields **deleted** on Gate 1 evidence (`InvoicedQty`/`BalanceToInvoiceQty`; no persisted source) and one action reason **deleted** (`cancellation pending`; no persisted field distinguishes it). `AsOfDate` moved onto `SaInquiryQuery` so the company-local clock enters as data instead of a new service dependency. Per-screen menu allow-list added (the §13.5 “wrong menuCode” row is now enforced, not just tested). |

## 0.2 Gate 1 / Gate 2 record (2026-09-26) — Phase A only

Gate 1 was executed against the shipped code before any Phase A line was written. Everything the plan
asserted in §11.0 was re-confirmed, and the items below changed the plan (Gate 2).

### Confirmed as planned (no change)

| Fact | Evidence |
|---|---|
| `SaDualStatuses` = `NONE`/`PARTIAL`/`FULL`/`WRITTEN_OFF`; `WRITTEN_OFF` terminal for billing | `ErpWeb.Core/Sales/ISaDocApplication.cs:13-25` |
| **`SaDo` has no header `InvNo`**; the invoice number is per line on `SaDoDetail.InvNo` | `SaDo.cs` (absent) / `SaDoDetail.cs:9` |
| `SaDo.PostedDate` is `DateTime?` | `SaDo.cs` |
| `SaSoDetail` persists `OrderQty`/`ShippedQty`/`DeliveredQty`/`InvoicedQty`/`BalanceQty`/`WrittenOffQty` **plus a second UOM family** (`StdQty`/`WtQty`, `SellingUom`/`StdUom`/`WtUom`) and `DeliveryDate` | `SaSoDetail.cs:13-36,55` |
| `SaQtStatuses.Live` includes `Expired`; `Expirable` = {New, Sent}; `Accepted` is never auto-expired | `SaQtCalc.cs:6-34` |
| `IrbmSentOn` is a real `DateTime?` on `SaInvoice` **and** `SaCdn` | `SaInvoice.cs:83`, `SaCdn.cs:51` |
| The e-Invoice registry row = highest `Id` for `(companyID, documentType, documentNo)` | `EInvDocSubmission.cs` |

### Changes forced by Gate 1 / Gate 2

1. **A2 `InvoicedQty` and `BalanceToInvoiceQty` are DELETED from the row DTO.** `SaDoDetail` persists
   neither an invoiced quantity nor a balance, and §6 A2 forbids manufacturing one. The screen shows the
   persisted delivered `Qty` plus the line's own `InvNo`; the summary reports
   `NotFullyInvoicedQty`/`NotFullyInvoicedValue` — the delivered quantity and net value **of the lines
   that still owe billing** — and is named exactly that, never “balance to invoice”.
2. **A4 `cancellation pending` is DELETED from the reason set.** `IrnmCancelOn` records a *completed*
   cancellation; no persisted field distinguishes “requested but unconfirmed”, so §7's rule (“a reason
   that needs a new interpretation of a status is a Gate 1 item, not an invented rule”) applies. The
   shipped reasons are: not submitted · pending &gt; 3 days (monitoring) · INVALID · FAILED ·
   status mismatch.
3. **`AsOfDate` is a property of `SaInquiryQuery`, not a new service dependency.** Adding
   `ICurrentDateService` to `SaSalesInquiryService` would have forced every construction site (including
   all test fixtures) to change; the Po inquiry already threads its company-local date as
   `PoInquiryQuery.AsOfDate`. The page therefore supplies `ICurrentDateService.Today`, and a monitor
   method falls back to `DateTime.Today` only when it is absent.
4. **The pending scope is the per-line invoice state, not the header rollup.** Gate 1 showed that a
   header-based “not FULL/WRITTEN_OFF” predicate still lists an **already invoiced** line of a `PARTIAL`
   order. The SQL predicate is now exactly the complement of `ResolveInvoiceState`:
   `BillingStatus <> WRITTEN_OFF && line InvNo is blank`. A `FULL` header whose line carries no invoice
   number is therefore still listed — that is the real inconsistency, not a false negative.
5. **Ageing/expiry filters are SQL ranges, not in-memory filters.** Because `AgeDays = asOf − SoDate`, a
   bucket is exactly a `SoDate` range, and “overdue” is exactly `DeliveryDate < asOf`; the same holds
   for `ValidUntil`. So paging stays server-side and the page is never materialised wholesale.
6. **Per-screen menu allow-list (new, §13.5).** Verified defect: one `KnownMenus` set for every method
   means a caller holding `SA_QT_EXPIRY` could read the SO-ageing screen. Every monitor method now
   resolves the tenant scope first, then ACCESS, then checks the resolved menu against **its own**
   screen; a borrowed code is refused with `Validation`. Pinned by `MonitorScreens_ServeOnlyTheirOwnMenu`.
7. **The A2 four-state strip and the e-Invoice strip refuse past the export cap** instead of summarising
   a partial window (a header that silently counted part of the window is worse than no header).

### Repo-wide observation (not fixed here, outside Phase A scope)

The **Phase-1** inquiry methods still share one `KnownMenus` set, so the same borrowed-menu weakness
applies to the seven shipped `SA_*` inquiry screens. Fixing it is a one-line-per-method change; it is
recorded here rather than silently left, and is deliberately not bundled into Phase A.


## 0.1 Review record — disposition of every review item

Received 2026-09-26 (score **9.2/10**); verbatim text in **Appendix A**. Nothing was deferred: each item is either applied to this plan or converted into a **Gate 1** verification obligation (§12).

| Review item | Disposition in REV 2 | Where |
|---|---|---|
| §2.1 Verify quantity semantics before B1 (UOM, sign, base vs transaction UOM, update lifecycle, partial/return/adjustment effects) | **Binding gate.** A persisted quantity may only back a rule whose semantics are established; unverifiable ⇒ that rule cannot be `Error`. | §3 (1a), §7, §11.0, §12 |
| §2.2 Rename A2 (a partially billed DO can legitimately carry an invoice number) | Renamed **Delivered Not Fully Invoiced**; per-line state `NO_INVOICE / PARTIAL / FULL / WRITTEN_OFF`; shows Invoiced Qty + Balance-to-Invoice Qty; **no new columns**. | §5, §6 A2, §11.0 |
| §2.3 A1 ageing terminology | UI vocabulary fixed: **SO Age** / **Delivery Due Date** / **Overdue Days**. `AgeDays` is never delivery ageing. | §4, §6 A1 |
| §2.4 Document the two QT expiry rules | Both rules re-specified against the shipped `SaQtStatuses.Live` / `Expirable`; they detect status↔date inconsistency, not duplicates — and are **not** errors. | §7 |
| §2.5 D5 is a monitoring threshold, not a business rule | Guardrail reworded; the 3-day wait is presentation-only and never modifies processing. | §3 (1b), §6 A4, §11.1 D5 |
| §2.6 Strengthen B1 validation | `Error` requires a documented engine invariant; otherwise the rule is omitted or downgraded to Warning/Info, and each rule must cite its evidence. | §3 (1a), §7 |
| §3.1 Rename "RFM-lite" | Screen is **Customer Sales Analytics**; "RFM" is used only if real R/F/M scoring ships. | §5, §8 C1 |
| §3.2 Split C1 visually | KPI strip / main grid / optional detail split specified (no single wide management grid). | §8 C1 |
| §3.3 Define the zero-prior growth case | `prior = 0 && current > 0` ⇒ Growth null + Trend `New`; `prior = 0 && current = 0` ⇒ null + `No Activity`; both tested. | §4, §8 C1, §10 |
| §3.4 Rename `ExposurePercent` | `PeriodSalesToCreditLimitPercent`, caption **"Period Sales / Credit Limit %"**; never labelled "Exposure %". | §4, §8 C1, §11.1 D8 |
| §4 C2 sales trend | Kept as designed; half-open period tests retained. | §8 C2 |
| §5 C3 tax analysis | Renamed **Sales Tax Analysis**; explicitly *not* an SST return/compliance report unless Gate 1 confirms the tax groups map to the SST classification. | §5, §8 C3 |
| §6 Chart on every screen | Demoted to a UX guideline — a chart is added where it carries decision value. | §3 (11), §9 |
| §7 Authorization testing | Automated matrix added (tenant / company / branch / missing ACCESS / wrong `menuCode` / export / scope-before-permission). | §10, §13.5 |
| §8 D1–D9 classification | Decisions split into **§11.1 business/product** and **§11.2 existing-system verification**. | §11 |
| §9 Build gate | §12 Gate 1 (verify) → Gate 2 (update this plan) → Gate 3 (implement). | §12 |
| §10 Most important areas first | Build order now begins with the gate. | §15 |

## 1. TL;DR

Add a new **`SA_MONITOR`** menu group with **8 read-only screens** built from existing columns only: open-order ageing & overdue delivery (SO age vs delivery due date), **delivered-not-fully-invoiced** leakage, quotation expiry watch, e-Invoice action queue, a cross-document **exception check**, **customer sales analytics** (growth/AOV/return/recency), a period-over-period **sales trend**, and (optional) **sales tax analysis**. No AR, no COGS, no schema change.

**REV 2's discipline:** every figure a screen shows is either (a) a persisted column rendered as-is, or (b) a clearly-labelled derived ratio — and every **`Error`** the exception screen raises must trace to a documented engine invariant, never to an assumption about how quantities behave.

## 2. Scope decisions (locked)

- **Screens are new, not edits to the Phase-1 listings.** The Phase-1 `SA_INQUIRY` screens remain the row-level drill-down.
- **New menu group `SA_MONITOR` ("Sales Monitor")**, SortOrder 5 under `SALES`. Every screen is **ACCESS-only** (read-only), CSV gated by the same ACCESS.
- **No AR** → no receivable outstanding/statement/aging/payment status. Where a "credit" figure appears it is the **period sales / credit limit %** proxy (D8), labelled exactly that.
- **No COGS** → no margin/profitability/contribution.
- **No schema change.** Everything comes from `SaInvoice`, `SaInvoiceDetail`, `SaSo`, `SaSoDetail`, `SaDo`, `SaDoDetail`, `SaQt`, `SaCdn`, `SaCust`, `SaSalesRep`, `SaDocApplication`, `EInvDocSubmission`, `IvStockMaster`/`IvClass`/`IvWarehouse`.
- **"Today" is company-local** via `ICurrentDateService` (not `DateTime.Today`).
- Ageing/date-diffs are **computed in memory after materialisation** (SQLite and SQL Server date functions differ); all *aggregation* stays server-side.
- **Monitoring thresholds are presentation rules only.** The windows and thresholds in this plan (3-day e-Invoice wait, age buckets, expiring-soon window, "lapsed", "new") are screen/monitoring vocabulary. They are never transactional business rules, never modify processing, and are never written back to a document.
- **Labels are part of the contract.** A caption may not overstate what a figure is: "Period Sales / Credit Limit %" is not AR exposure; "Sales Tax Analysis" is not an SST return.
- **Renames in REV 2 are free:** none of these menus, routes, DTOs, services or pages has shipped, so the A2/C1/C3 naming below is final and consistent everywhere (menu code, route, caption, DTO, page, test class).

## 3. Implementation Guardrails (binding)

1. No new columns, statuses, or business rules. Every "exception" rule must be a direct check of the engine's documented quantity/status semantics.
   1a. **Semantics before rules.** Every persisted quantity or status used in a comparison must be verified against its production semantics — UOM family, sign convention, base vs transaction UOM, update lifecycle, and the effect of partial / return / adjustment transactions — **before** the rule is implemented. When the semantics cannot be established, the rule is not implemented as an `Error` (see the §7 `Error` contract).
   1b. **Monitoring ≠ business rule.** The thresholds in §11.1 (D2 buckets, D5 3-day wait, expiring window, lapsed) are presentation/monitoring rules only and must never modify transactional processing.
2. Tenant scope first (fail closed), then ACCESS on the caller's own menu — the existing `IvInquiryScopeResolver` order.
3. All aggregation server-side; no N+1; one bounded query per aggregate; batch lookups only.
4. Every new method takes `menuCode` (own grant on its own menu).
5. Read-only — never mutate transactional data.
6. CSV export calls the **same** service method and filter as the grid; refused (not truncated) past 50 000 rows.
7. `BalanceQty`/`ShippedQty`/`DeliveredQty`/`InvoicedQty`/`WrittenOffQty` and header `FulfillmentStatus`/`BillingStatus` are **persisted** — shown as-is, never recomputed.
   7a. **UOM discipline:** these SO line quantities are the *selling/transaction UOM* family (`SaSoDetail.SellingUom`). `StdQty`/`WtQty` (with `StdUom`/`WtUom`) are a different family and must never be compared with them or with an invoice line's `Qty`. A quantity whose UOM family cannot be established may not generate an exception.
8. CN/DN stored positive; net once (`INV + DN − CN`).
9. Reuse `SaSalesAnalysisService` result shapes for anything that overlaps the analysis screens.
10. New pattern (`AgeBucket`, `GrowthPercent`, `TrendDirection`, `Severity`) is UI vocabulary only — never persisted.
11. Charts are a **UX choice, not an architectural requirement** — add one where it carries decision-support value (§9).

## 4. Locked semantics (definitions used everywhere)

| Term | Definition | Source |
|---|---|---|
| **SO age days** | `(today − SoDate.Date).Days` — the age of the *order*. Never call it, or caption it, delivery ageing. | in memory |
| Age bucket | `0–30 / 31–60 / 61–90 / >90` (D2 — monitoring vocabulary) | in memory |
| **Delivery due date** | `SaSoDetail.DeliveryDate` (verified column, line level) — the *expected delivery* date | entity |
| **Overdue days** | `(today − DeliveryDate.Date).Days` on an open SO line, `> 0` only (`DeliveryDate = today` is **not** overdue) | in memory |
| **Delivered not fully invoiced** | DO `Status = POSTED` **and** header `BillingStatus ∉ {FULL, WRITTEN_OFF}` (D4), with the per-line state below | `SaDualStatuses` (`NONE`/`PARTIAL`/`FULL`/`WRITTEN_OFF` — verified) |
| Line invoice state | `SaDoDetail.InvNo` blank & header `NONE` ⇒ `NO_INVOICE`; `InvNo` blank & header `PARTIAL` ⇒ `PARTIAL`; `InvNo` set ⇒ `INVOICED`; header `WRITTEN_OFF` ⇒ `WRITTEN_OFF`. Header `BillingStatus` is the rollup; **`SaDo` has no header `InvNo` column** (verified — `SaDoDetail.InvNo` only). | `SaDo` / `SaDoDetail` |
| Balance to invoice | `Qty − invoiced-portion` for the line, derived in memory and labelled as derived; **no new column** | in memory |
| Days since delivered | `(today − (Do.PostedDate ?? Do.DoDate).Date).Days` (D3) | in memory |
| Expiring soon | live QT with `0 ≤ DaysToExpiry ≤ 7` (D5-style window) | `ValidUntil` |
| Expired QT | `today > ValidUntil`; the status is expected to be `EXPIRED` (the lazy sweep moves `NEW`/`SENT` there — `SaQtStatuses.Expirable`, verified) | `ValidUntil` |
| AOV | `NetSales ÷ InvoiceCount` (null when count = 0) | derived |
| Return rate | `CreditNoteTotal ÷ InvoiceTotal × 100` (null when InvoiceTotal = 0) | derived |
| **Growth %** | `prior > 0` ⇒ `(cur − prior) ÷ prior × 100`; `prior = 0 && cur > 0` ⇒ `null` + Trend `New`; `prior = 0 && cur = 0` ⇒ `null` + Trend `No Activity`. Prior = equal-length window (D6). | derived |
| Recency | `(today − LastInvoiceDate).Days` (null when never invoiced) | derived |
| **Period sales / credit limit %** | `period NetSales ÷ SaCust.CreditLimit × 100` — a **period proxy, NOT receivables exposure** (D8) | derived |

## 5. New screen catalogue

| # | Menu code | Route | Screen | Primary user |
|---|---|---|---|---|
| A1 | `SA_SO_AGEING` | `/sales/monitor/so-ageing` | Open SO ageing & overdue delivery | Sales ops |
| A2 | `SA_DO_NOT_FULLY_INVOICED` | `/sales/monitor/delivered-not-fully-invoiced` | **Delivered Not Fully Invoiced** (leakage) | Finance / sales ops |
| A3 | `SA_QT_EXPIRY` | `/sales/monitor/quotation-expiry` | Quotation expiry watch | Sales |
| A4 | `SA_EINV_ACTION` | `/sales/monitor/einvoice-action` | e-Invoice action queue | Finance |
| B1 | `SA_SALES_EXCEPTION` | `/sales/monitor/exceptions` | Sales exception check | Support / audit |
| C1 | `SA_CUST_ANALYTICS` | `/sales/monitor/customer-analytics` | **Customer Sales Analytics** | Marketing / management |
| C2 | `SA_SALES_TREND` | `/sales/monitor/sales-trend` | Sales trend & comparison | Management |
| C3 | `SA_TAX_ANALYSIS` | `/sales/monitor/tax-analysis` | **Sales Tax Analysis** *(optional)* | Finance |

## 6. Phase A — Ageing & action queues

### A1 · SO Ageing & Overdue
- **DTO** `SaMonitorResults.cs`: `SaSoAgeingRow` { SoNo, Rev, SoDate, AgeDays, AgeBucket, Status, FulfillmentStatus, BillingStatus, CustCode, CustName, SalesRep, TotAmnt, Line, ICode, IDesc, OrderQty, DeliveredQty, InvoicedQty, BalanceQty, DeliveryDate, IsOverdueDelivery, OverdueDays }; `SaSoAgeingSummary` { OpenCount, OriginalValue, OutstandingValue, PendingQty, OverdueQty, Buckets: `SaAgeBucketRow{Label,Count,Value,Qty}[]` }.
- **Vocabulary (review §2.3).** Captions are fixed: `AgeDays` → **"SO Age (days)"**, `DeliveryDate` → **"Delivery Due Date"**, `OverdueDays` → **"Overdue Days"**. `AgeDays` is the age of the order and is *never* delivery ageing; the grid must not show a bare "Age" column beside a delivery date. `OverdueDays` is derived from `DeliveryDate` only, and `DeliveryDate = today` is **not** overdue.
- **Service** `ISaSalesInquiryService.GetSoAgeingAsync(menuCode, SaInquiryQuery, ct)` → `IvMasterOperationResult<SaInquiryPage<SaSoAgeingRow>>`; plus `GetSoAgeingSummaryAsync(menuCode, query, ct)`.
- Scope: `IsCurrent` SO lines; optional `status`, `overdueOnly`, `bucket`, customer, salesman, branch, date range on `SoDate`. `OutstandingValue`/`PendingQty` reuse the Phase-1 persisted-quantity conventions — nothing is recomputed.
- Pages: `ErpWeb.UI/Sales/Monitor/SaSoAgeing.razor(.cs)` — chips (open count/value/outstanding/pending/overdue) + SO-age bucket bars + grid with the three distinct ageing columns.

### A2 · Delivered Not Fully Invoiced *(renamed, review §2.2)*
- **Why renamed:** a DO with `BillingStatus = PARTIAL` legitimately has *some* invoice numbers while an **uninvoiced balance** remains. "Delivered-not-invoiced" mis-describes that as a missing invoice. The screen reports **not fully invoiced**.
- **DTO** `SaDoNotFullyInvoicedRow` { DoNo, DoDate, PostedDate, DaysSinceDelivered, Status, BillingStatus, InvoiceState, CustCode, CustName, SalesRep, TotAmnt, Line, ICode, IDesc, Qty, InvoicedQty, BalanceToInvoiceQty, SoNo, LineInvNo, IsPendingInvoice }; `SaDoNotFullyInvoicedSummary` { DoCount, LineCount, PendingQty, PendingValue, Buckets[] }.
- **`InvoiceState`** (display vocabulary, in memory): `NO_INVOICE` / `PARTIAL` / `INVOICED` / `WRITTEN_OFF`, derived from `SaDoDetail.InvNo` + the header rollup — see §4. The grid shows all four as a filter; `pendingOnly` keeps `{NO_INVOICE, PARTIAL}`.
- **Shown quantities:** `Qty` (delivered) and `InvoicedQty`/`BalanceToInvoiceQty` where the existing columns carry them; anything derived is captioned as derived. **No schema change** — if a balance figure cannot be derived from existing columns without inventing a rule, the column is dropped rather than computed by a new formula (Gate 1).
- **Service** `GetDeliveredNotFullyInvoicedAsync(menuCode, query, ct)` + `…SummaryAsync`.
- Default filter `pendingOnly = true`.
- Page `ErpWeb.UI/Sales/Monitor/SaDoNotFullyInvoiced.razor(.cs)`; test class `SaDoNotFullyInvoicedTests`.

### A3 · Quotation Expiry Watch
- **DTO** `SaQtExpiryRow` { QtNo, Rev, QtDate, ValidUntil, DaysToExpiry, ExpiryBucket, Status, ConversionStatus, CustCode, CustName, SalesRep, TotAmnt, IsExpired, IsExpiringSoon }; `SaQtExpirySummary` { OpenCount, OpenValue, ExpiringSoonCount, ExpiringSoonValue, ExpiredCount, ExpiredValue, BySalesRep[] }.
- **Service** `GetQtExpiryAsync(menuCode, query, ct)`. `IsCurrent` revisions only; statuses are read from `SaQtStatuses` (verified: `Live` = current-revision set; `Expirable` = `{NEW, SENT}` for the lazy-expiry sweep; `ACCEPTED` is never auto-expired). `today > ValidUntil`, `EXPIRED` and `NEW/SENT` are all **normal** states of that lifecycle, so this screen is a *watchlist*, not an exception list.

### A4 · e-Invoice Action Queue
- **DTO** `SaEInvoiceActionRow` { DocType, DocNo, DocDate, CustCode, CustName, IrbmStatus, NormalizedStatus, LatestSubmissionStatus, StatusLabel, DaysSinceSubmitted, ActionReason }; `SaEInvoiceStatusBreakdown` { counts per status + NeedsActionCount }.
- **Service** `GetEInvoiceActionQueueAsync(menuCode, query, ct)` + `GetEInvoiceStatusBreakdownAsync(menuCode, query, ct)`.
- Reuses the existing latest-`EInvDocSubmission`-row join + `EInvoiceStatuses.Normalize`. Read-only: the queue never calls the portal, never re-submits, and never writes to `InvoicedQty`/`EInvDocSubmission`.
- `ActionReason` ∈ {not submitted, **monitoring: pending > 3 days (D5)**, INVALID, FAILED, cancellation pending, status mismatch}. `DaysSinceSubmitted` derives from `IrbmSentOn` — a real `DateTime?` column on `SaInvoice` and `SaCdn` (verified). The 3-day window is a **presentation threshold (D5)**, named in the UI as a monitoring hint, never a business rule (§3 1b).
- Every reason string must be enforceable from a single persisted field comparison — anything that needs a new join or a new interpretation of a status becomes a Gate 1 item, not an invented rule.

## 7. Phase B — Sales Exception Check

### 7.1 The `Error` contract (review §2.6 — binding)

> An exception may be classified as `Error` **only** when the existing ERP transaction/posting logic establishes that the condition is invalid. Otherwise it must be omitted, or downgraded to a review-only `Warning`/`Info`.

- Every shipped rule carries an **Evidence** entry: the file + symbol that makes the condition impossible or merely suspicious. A rule with no evidence cannot ship as `Error`.
- `Severity` is reporting vocabulary. It never blocks a save, a post or a submission, and it is never persisted.
- One bounded query per rule; each rule is a direct comparison of persisted columns in **one UOM family** (§3 7a).
- **Rule removals beat re-wording:** a rule that fails its Gate 1 evidence requirement is deleted from the rule set. A monitor that reports "probably" as an error is worse than one with three fewer rules.

- **DTO** `SaSalesExceptionRow` { Category, DocType, DocNo, Line, Rule, Severity (`Error|Warning|Info`), Detail, **Evidence**, DocDate, CustCode, CustName, Value, Qty }; `SaSalesExceptionSummary` { counts by Severity and by Rule }.
- **Service** `GetSalesExceptionsAsync(menuCode, query, ct)` → `SaInquiryPage<SaSalesExceptionRow>` + `…SummaryAsync`.

### 7.2 Rule set — re-specified, with the verification each rule still needs

| Category | Rule code | Check | Severity in REV 2 | Evidence / Gate 1 requirement |
|---|---|---|---|---|
| SO | `SO_CLOSED_WITH_BALANCE` | `Status = CLOSED && BalanceQty > 0` | **Warning** (was Error) | A force-closed SO line is *expected* to keep a positive `BalanceQty` when the remainder was written off (`SaSoClosedReasons.ForceClosed` + `WrittenOffQty` accrual). Refine to `BalanceQty > 0 && WrittenOffQty = 0` **only if** `SaSoCalc`/`SaSoQty` proves the refined form cannot occur. |
| SO | `SO_OVER_SHIPPED` | `ShippedQty > OrderQty` | **Info** unless proven | `SaSoQty` maintains `BalanceQty` from `DeliveredQty` *or* `ShippedQty` depending on the operation, so these quantities are not one monotonic chain. Needs the ship-path invariant. |
| SO | `SO_OVER_DELIVERED` | `DeliveredQty > ShippedQty` | **Info** unless proven | Do not assume delivery ≤ shipped without engine evidence. |
| SO | `SO_OVER_INVOICED` | `InvoicedQty > DeliveredQty` | **Info** unless proven | Billing is driven by DO consumption (`SoConsumedQty`), not necessarily by delivery. |
| SO | `SO_OVER_WRITTEN_OFF` | `WrittenOffQty > OrderQty` | **Error** (eligible) | `SaSoCalc` **I1** documents exactly this ceiling — cite it. Cross-check `SaAllocationReconciliationService` (`WRITTEN_OFF_QTY_MISMATCH`). |
| SO | `SO_NEGATIVE_BALANCE` | `BalanceQty < 0` | **Info** unless proven | Same evidence bar as the over-\* rules. |
| DO | `DO_FULL_NO_INVOICE` | `Status = POSTED && BillingStatus = FULL && SaDoDetail.InvNo blank` | Warning | `FULL` is set from actual billing (`SaDocApplicationService`) and `WRITTEN_OFF` is a distinct terminal state, so this is a real inconsistency — but it **must read the detail column**: `SaDo` has no header `InvNo` (verified; only `SaDoDetail.InvNo` exists). |
| INV | `INV_LINKDO_NO_DONO` | `SaInvoiceDetail.LinkDo && DoNo blank` | **Error** (eligible) | Two columns on the same row, written together by the allocation path. **Never use `SaInvoice.DoNo`** — it is a legacy column reuse holding the *invoice number*, so a header check would fire on every invoice. |
| INV | `INV_SOCONSUMED_NO_SONO` | `SoConsumedQty > 0 && SoNo blank` | **Error** (eligible) | `SaInvoiceDetail.SoNo` is a non-null string defaulting to `""` and is written with `SoConsumedQty`. |
| INV | `INV_NONPOSITIVE_QTY` | `Qty <= 0` | **Warning** | Suspicious, but no documented invariant forbids it (correction lines exist). Keep non-`Error`. |
| CDN | `CDN_NONPOSITIVE_TOTAL` | `TotAmnt <= 0` | **Warning** | Verify against `SaCdnCalc` sign conventions before any promotion. |
| CDN | `CDN_UNKNOWN_TYPE` | `Type ∉ SaCdnTypes` | **Warning** until verified | Verify the shipped token set first; until then a legacy token must not inflate the error count. |
| QT | `QT_CURRENT_EXPIRED` | `IsCurrent && Status = EXPIRED` | **Info** | This is the **normal** post-sweep state (`SaQtStatuses.Live` contains `Expired`; the lazy sweep moves `New`/`Sent` there). A watchlist reminder, never an error. |
| QT | `QT_UNSWEPT_EXPIRY` | `IsCurrent && Status ∈ {NEW, SENT} && ValidUntil < today` | **Info** | The genuine status↔date inconsistency: open by status, past validity. `SaQtStatuses.Expirable` is the sweep set; `Accepted` is never auto-expired. |
| EINV | `EINV_STATUS_NO_ROW` | `IRBMStatus` set && no registry row | **Warning** | Registry = highest-`Id` `EInvDocSubmission` row for `(companyID, documentType, documentNo)`. Read-only. |
| EINV | `EINV_ROW_NO_STATUS` | registry row && `IRBMStatus` blank | **Warning** | As above. |

- Page `ErpWeb.UI/Sales/Monitor/SaSalesException.razor(.cs)`: severity chips (default `Warning`+), rule filter, grid, and a visible `Evidence` column so support can see *why* a rule exists.

## 8. Phase C — Customer & trend analytics

### C1 · Customer Sales Analytics *(renamed, review §3.1)*
- **Naming:** the screen is **Customer Sales Analytics**. "RFM" may appear **only** if real Recency/Frequency/Monetary scoring and segmentation are implemented; the shipped fields are descriptive metrics, so the caption must not promise RFM.
- **DTO** `SaCustomerAnalyticsRow` { CustCode, CustName, SalesRep, CustGroup, CustType, Area, InvoiceCount, InvoiceTotal, CreditNoteTotal, ReturnRatePercent, Aov, MonthsActive, Frequency, FirstInvoiceDate, LastInvoiceDate, RecencyDays, PriorNetSales, GrowthPercent, TrendDirection, CreditLimit, **PeriodSalesToCreditLimitPercent** }; `SaCustomerAnalyticsSummary` { CustomerCount, ActiveCount, NewCount, LapsedCount, NetSales, Top10SharePercent, AvgAov, AvgReturnRate }.
- **Service** `GetCustomerAnalyticsAsync(menuCode, query, ct)` + `…SummaryAsync`.
- Filters: date range (+ auto prior equal-length window), customer, salesman, group/type/area, min value, "lapsed only" / "new only". Rows are POSTED-invoice-only; CN/DN joined for return rate.
- **Layout — three zones, never one wide grid (review §3.2):**
  - **KPI strip:** Customer Count, Active Customers, New Customers, Lapsed Customers, Net Sales, AOV.
  - **Main grid:** Customer, Sales Rep, Invoice Count, Net Sales, AOV, Return Rate, Growth, Recency.
  - **Optional/detail columns** (toggled off by default): Frequency, First Invoice, Last Invoice, Credit Limit, Period Sales / Credit Limit %.
- **Growth when the prior period is zero (review §3.3, D6):** `prior > 0` ⇒ `GrowthPercent` computed; `prior = 0 && current > 0` ⇒ `GrowthPercent = null`, `TrendDirection = New`; `prior = 0 && current = 0` ⇒ `GrowthPercent = null`, `TrendDirection = No Activity`. `TrendDirection` ∈ {`Up`, `Down`, `Flat`, `New`, `No Activity`} — display vocabulary only. Each case has a test (§10).
- **Credit proxy label (review §3.4, D8):** the field is `PeriodSalesToCreditLimitPercent`, captioned **"Period Sales / Credit Limit %"**, with a hint that it is a period-sales ratio and **not** receivable or credit-limit utilisation. A blank/null `SaCust.CreditLimit` ⇒ null, never `0` or `∞`. If the business finds it unhelpful it can be removed until AR exists — it is a single toggle in the detail zone.

### C2 · Sales Trend & Comparison
- **DTO** `SaSalesTrendRow` { PeriodLabel, PeriodStart, InvoiceCount, InvoiceTotal, CreditNoteTotal, DebitNoteTotal, NetSales, PriorNetSales, GrowthPercent, TrendDirection }; `SaSalesTrendResult` { Rows, Totals }.
- **Service** `ISaSalesAnalysisService.GetSalesTrendAsync(menuCode, query, SaTrendGranularity, ct)` — `SaTrendGranularity { Day, Month, Year }` (default Month).
- Grouping in SQL (`Year/Month` or `Date`); prior period resolved in memory. Half-open period boundaries; the zero-prior growth rule above applies here too.

### C3 · Sales Tax Analysis *(renamed, review §5 — optional, D9)*
- **Naming:** invoice **tax** analysis. It is **not** an SST return or a compliance report unless Gate 1 confirms that `SaTaxGroup` / `IvMSCode(CodeType='TAX')` values represent the SST reporting classification required by the return. The page carries a subtitle saying which claim it can support.
- **DTO** `SaTaxAnalysisRow` { TaxGrCode, TaxGrDesc, InvoiceCount, NetAmount, TaxAmount, GrossAmount, SharePercent }.
- **Service** `GetTaxAnalysisAsync(menuCode, query, ct)` — group POSTED invoice lines by `SaInvoiceDetail.TaxGrCode`.

## 9. Phase D — Charts *(UX guideline, not a structural requirement — review §6)*

> Each monitor screen should include a chart **where it provides meaningful decision-support value**. Charts must not be added merely to satisfy a structural requirement.

Suggested set (drop any that does not earn its place): A1 SO-age bucket bars, A2 leakage bars, A3 expiry bars, A4 status breakdown, B1 severity bars, C1 top-customers bar, C2 trend line, C3 tax bars. If the business explicitly wants all 8 screens charted, that is a UX decision and the set above becomes the requirement.

Series fields are **expression lambdas** (`ArgumentField="@((Row x) => x.Key)"`) — not `@nameof(...)` strings (verified trap).

## 10. Shared plumbing

- **Files**
  - `ErpWeb.Core/Sales/SaMonitorResults.cs` — all Phase A/B/C DTOs.
  - `ErpWeb.Core/Sales/SaSalesInquiryService.cs` (+ `ISaSalesInquiryService.cs`) — Phase A/B methods. The class is `sealed` with a 3-arg constructor; **add `ICurrentDateService`** and update its test construction sites. Reuse the existing `MaxExportRows = 50_000` constant rather than declaring a second cap.
  - `ErpWeb.Core/Sales/SaSalesAnalysisService.cs` (+ interface) — C2/C3 methods (`menuCode` parameter like `GetSalesDetailAsync`; the class is `sealed`).
  - `ErpWeb.UI/Sales/Monitor/` — `SaMonitorPageBase.cs` + 8 `.razor(.cs)` pages (`SaDoNotFullyInvoiced` is the A2 page name).
  - `ErpWeb/Sales/SaInquiryExportEndpoints.cs` / `SaAnalysisExportEndpoints.cs` — CSV routes.
  - `ErpWeb.Core/Menus/MenuCodes.cs` — 9 new constants (`SA_MONITOR` + 8).
  - `ErpWeb/Menus/menus.xml` — the `SA_MONITOR` group; `scripts/init-sales-monitor-menu.sql` (ACCESS-only, idempotent).
  - `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — no new registration (services extended).
- **Tests** (SQLite; `Category=Sales` + a new screen trait `SalesMonitor`)
  - `SaSoAgeingTests` — bucket boundaries (30/31, 60/61, 90/91), overdue boundary (delivery due date = today is **not** overdue), open/closed, isolation, and that `AgeDays` is never derived from `DeliveryDate`.
  - `SaDoNotFullyInvoicedTests` — `InvoiceState` for all four states in §4, pending definition (`FULL`/`WRITTEN_OFF` excluded), days-since-delivered fallbacks (`PostedDate` null), and that a `PARTIAL` DO with *some* invoice numbers is **not** reported as missing an invoice.
  - `SaQtExpiryTests` — expiring-soon boundary (≤7), `EXPIRED` treated as the normal post-sweep state, `ACCEPTED` never expiring, value-at-risk.
  - `SaEInvoiceActionQueueTests` — each `ActionReason`, the 3-day **monitoring** threshold boundary, legacy casing via `Normalize`, and that the queue writes nothing.
  - `SaExceptionCheckTests` — one test per rule (true positive + a clean document not flagged) **plus a severity-contract test**: no rule may emit `Error` without a non-empty `Evidence`.
  - `SaCustomerAnalyticsTests` — AOV/return-rate/growth/recency, null-when-denominator-zero, prior-window comparison, lapsed/new, and the **two zero-prior growth cases** (`New`, `No Activity`), plus null (not 0) when `CreditLimit` is blank.
  - `SaSalesTrendTests` — granularity, half-open periods, growth, prior-period alignment, zero-prior periods.
  - `SaMonitorAuthorizationTests` — the §13.5 matrix (tenant / company / branch / missing ACCESS / wrong `menuCode` / export / scope-before-permission).
- **Menus** follow the triad: `MenuCodes` + `menus.xml` + `init-sales-monitor-menu.sql` (guarded by `MenuDeploymentParityTests`).

## 11. Decisions

### 11.0 Verified facts (read from the workspace on 2026-09-26 — do not re-research)

| Fact | Evidence |
|---|---|
| `SaDualStatuses` = `NONE` / `PARTIAL` / `FULL` / `WRITTEN_OFF`; `WRITTEN_OFF` is documented as terminal for billing | `ErpWeb.Core/Sales/ISaDocApplication.cs:13-25` |
| `SaSoDetail` persists `OrderQty`, `ShippedQty`, `BalanceQty`, `DeliveredQty`, `InvoicedQty`, `WrittenOffQty`, **and** `StdQty`/`WtQty` with `SellingUom`/`StdUom`/`WtUom`; `WrittenOffQty` is monotonic and written only by `SaDoService.ForceCloseOneAsync` under the DO → SO lock order | `ErpWeb.Model/Entities/Sales/SaSoDetail.cs:13-36,55` |
| `SaSoDetail.DeliveryDate` exists (line-level planning column) | `SaSoDetail.cs:55` |
| **`SaDo` has NO header `InvNo`** — the invoice number is per line on `SaDoDetail.InvNo` (nullable) | `SaDo.cs` (absent) / `SaDoDetail.cs:9` |
| **`SaInvoice.DoNo` is a legacy column reuse that stores the invoice number**, not a DO reference; DO linkage is `SaInvoiceDetail.LinkDo` + `DoNo` + `DoLine` | `SaInvoice.cs:12-16`; `SaInvoiceDetail.cs:38-42` |
| `SaInvoiceDetail.SoNo` is a non-null string (`""` default) beside `SoConsumedQty`; `SaDoDetail.SoConsumedQty` exists too | `SaInvoiceDetail.cs:33-42`, `SaDoDetail.cs:50` |
| `SaQtStatuses`: `Live` = {New, Sent, Accepted, Cancelled, Lost, Expired, Closed}; `Revisable` = {New, Sent, Accepted}; **`Expirable` = {New, Sent}` (the lazy-expiry sweep) and `Accepted` is never auto-expired** | `ErpWeb.Core/Sales/SaQtCalc.cs:6-34` |
| `SaCust.CreditLimit` is `decimal?` | `ErpWeb.Model/Entities/CustomerProfile/SaCust.cs:57` |
| `IrbmSentOn` is a real `DateTime?` column on `SaInvoice`, `SaCdn` (and the purchase/self-billed families) | `ErpWeb.Model/Entities/Sales/SaInvoice.cs`, `SaCdn.cs:51` |
| `SaSalesInquiryService` and `SaSalesAnalysisService` are both `sealed`; the inquiry service already declares `MaxExportRows = 50_000` and the gating order (tenant first, fail closed, then ACCESS) | `SaSalesInquiryService.cs:23-52`, `SaSalesAnalysisService.cs:28` |
| `IvInquiryScopeResolver.ResolveAsync(tenant, accessRights, menuCode, KnownMenus, ct)` is the shared gate every inquiry service reuses | `ErpWeb.Core/Inventory/IvInquiryScopeResolver.cs:38` |

### 11.1 Business / product decisions — still need sign-off

| # | Type | Decision | Recommendation |
|---|---|---|---|
| **D1** | Product | New `SA_MONITOR` group vs spreading into `SA_ANALYSIS`/`SA_INQUIRY` | **New group** |
| **D2** | Product | Ageing buckets | **0–30 / 31–60 / 61–90 / >90** (monitoring vocabulary) |
| **D5** | Product | e-Invoice "pending too long" threshold | **3 days** from `IrbmSentOn` — **a monitoring threshold (presentation only)**, never a business rule |
| **D6** | Product | Growth comparison window, incl. the zero-prior display rule | **prior equal-length window**, with `New` / `No Activity` for `prior = 0` (§8 C1) |
| **D8** | Product | Credit-limit period proxy (`PeriodSalesToCreditLimitPercent`) | **Include, clearly labelled**; removable until AR exists |
| **D9** | Product | Phase C3 screen | **Include** as **Sales Tax Analysis** (invoice tax, not an SST return — §8 C3) |

### 11.2 Existing-system semantic verification — verify, do not decree

These are **not** preference decisions. Each must be answered from the shipped code/database during Gate 1 (§12), and the answer recorded in this plan before the dependent rule or screen ships.

| # | Question | Blocks | Already known |
|---|---|---|---|
| **D3** | Is `PostedDate ?? DoDate` the right "delivered on" source, and when is `PostedDate` null? | A2 `DaysSinceDelivered` | `PostedDate` exists on `SaDo`; the fallback must be re-checked on post/rollback |
| **D4** | What exactly does each `BillingStatus` value mean, and who writes it? | A2 pending definition | Set by the allocation path (`SaDocApplicationService` sets `None`/`Partial`/`Full`); `WRITTEN_OFF` is set by DO force-close. Confirm there is no other writer. |
| **D7** | Are `Info`-level exceptions wanted in the grid, and does any consumer treat severity as a gate? | B1 chips/CSV | No consumer exists yet — this is a display choice, and severity must never gate a write |
| **D10** | Do the persisted SO line quantities form the invariants the rules assume (UOM family, sign, lifecycle, partial/return/adjustment effects)? | **every B1 SO rule** | `SaSoQty` maintains `BalanceQty` from `DeliveredQty` *or* `ShippedQty` depending on the operation, so the four quantities are not one monotonic chain (§7.2) |
| **D11** | Do `SaTaxGroup` / `IvMSCode(CodeType='TAX')` values represent the SST reporting classification? | C3's caption and scope | Tax **type** is `IvMSCode(TAX)`; the invoice line groups by `SaTaxGroup.TaxGrCode`. Confirm before any "SST" wording. |

## 12. Build gate — semantic verification pass (review §9, binding)

**No Phase B rule, and no C3 screen, may be coded before Gate 1 passes.** This is a gate, not a review note.

### Gate 1 — Verify against the existing code (read-only)

| # | Verify | Where |
|---|---|---|
| 1 | SO quantity lifecycle: which path writes `ShippedQty`, `DeliveredQty`, `InvoicedQty`, `WrittenOffQty`, `BalanceQty`; the UOM family of each; the sign convention | `SaSoQty`, `SaSoCalc` (I1), `SaSoService`, `SaDoService.ForceCloseOneAsync`, `SaSoRevisionUsage` (D15) |
| 2 | DO billing semantics: every writer of `BillingStatus`; `PostedDate` nullability; `SaDoDetail.InvNo` lifecycle | `SaDocApplicationService`, `SaDoService` |
| 3 | Quotation status lifecycle and the lazy-expiry sweep | `SaQtStatuses.Live`/`Expirable`, `SaQtValidity`, `SA_QUOTE_VALID_DAYS` |
| 4 | e-Invoice status/submission model: `Normalize`, latest-row rule, `IrbmSentOn` semantics, cancellation statuses | `EInvoiceStatuses`, `SaEInvoiceService`, `EInvDocSubmission` |
| 5 | CN/DN sign conventions and the shipped type token set | `SaCdnCalc`, `SaCdnTypes` |
| 6 | Invoice line semantics: `Qty` vs `StdQty`, `LinkDo`/`DoNo`, `SoConsumedQty`/`SoNo` | `SaInvoiceDetail`, `SaInvoiceService` |
| 7 | Tax-group semantics (classification vs tax type) | `SaTaxGroup`, `IvMSCode(TAX)` |
| 8 | Tenant/company/branch filtering and the existing analysis result shapes | `IvInquiryScopeResolver`, `SaSalesInquiryService`, `SaSalesAnalysisService` |

### Gate 2 — Update this plan

If any assumption differs from the shipped code, **the plan is updated first** (change log + the rule/§ it affects). The plan, not the code, is what must give way.

### Gate 3 — Only then implement

The implementation agent may **not** invent a field, status, calculation or transactional rule to make a screen work. If a screen cannot be built from verified semantics, it is descoped and reported — not improvised.

## 13. Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 `error CS`/`RZ` (Razor is only compiled here; a green test run does **not** prove a page renders).
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"` → the existing 15-failure baseline unchanged; every new screen test green.
3. Apply `scripts/init-sales-monitor-menu.sql` **twice** on scratch, then once on dev `ERPWeb`; confirm 8 ACTIVE `MenuPermission` ACCESS rows; `MenuDeploymentParityTests` passes.
4. Browser smoke: each screen renders with date + customer + salesman; empty (0 rows), 1 row, and large result sets; CSV matches the grid; permission-denied reaches `/unauthorized` and the export returns a problem/403.
5. **Semantics evidence ships with the code:** each B1 rule's `Evidence` value and each Phase A/C derived column's comment names the file + symbol verified in Gate 1.

### 13.5 Authorization test matrix (automated — review §7)

Every row is a test in `SaMonitorAuthorizationTests`; each asserts the **fail-closed** outcome, not merely "no rows".

| Case | Expected |
|---|---|
| Wrong tenant / company | No rows; no leak of another company's document numbers |
| Wrong branch | No rows (scope resolves the caller's branch) |
| Missing ACCESS on the screen's own menu | Refused, never a partial result |
| Wrong `menuCode` (another screen's code, or one absent from `KnownMenus`) | Refused — a page cannot borrow another screen's rights |
| Export path under the same denial | Problem/403, never a truncated file |
| Tenant scope resolved **before** permission and data retrieval | Asserted by ordering: an unresolvable tenant scope must not reach the permission or data query |

## 14. Report → screen mapping (closes the original matrix gaps)

| Original report | Covered by |
|---|---|
| SO Aging / Expected Delivery / Cancelled / Fulfillment | A1 (SO age and delivery due date kept distinct) |
| DO Pending Invoice / Partial Delivery / Performance | A2 (**Delivered Not Fully Invoiced**) |
| QT By Customer / Aging / Trend | A3 |
| e-Invoice Dashboard / Exception Aging | A4 |
| *(new)* cross-document integrity | B1 |
| Customer Trend / Frequency / AOV / Return | C1 (**Customer Sales Analytics**) |
| Customer Credit-Limit Inquiry | C1 (period sales / credit limit % — a labelled proxy, not AR) |
| Sales Trend | C2 |
| Discount / SST-Tax / ASP | Phase 2 detail rows + C3 (**Sales Tax Analysis**) |
| Statement / Outstanding / Aging / Payment Status / AR | **still gated — no AR** |
| Margin / Profitability / Contribution | **still gated — no COGS** |

## 15. Build order

**Gate 1 → Gate 2 → Gate 3, then** `Phase A → Phase B → Phase C → Phase D`, each independently verifiable (build + its test class green) so you can stop after any phase.

The gate runs **first** because it is the only step that can change this plan: it resolves §11.2 (D3, D4, D7, D10, D11) and confirms every Phase B severity before Phase B is written. Phase A and C1/C2 depend on the same verification for their derived columns, so they follow it rather than preceding it.

---

## 16. Phase A — implementation status (2026-09-26)

**A1–A4 are IMPLEMENTED, built and tested.** Verified: `ErpWeb.Core` / `ErpWeb.UI` / `ErpWeb.Tests` all
build with 0 errors; `ErpWeb -t:Compile` succeeds; `dotnet test --filter "Category!=SqlServer"` =
**2376 total / 2361 passed / 15 failed / 0 skipped**, where the 15 are exactly the documented pre-existing
baseline (9 `SaCustServiceTests` + 4 `PoSupplierServiceTests` + 2 library `InvoiceTypeCode`) → **+63 new
tests, 0 regressions**. `Category=Menus` 24/24.

| Artefact | Delivered |
|---|---|
| DTOs | `ErpWeb.Core/Sales/SaMonitorResults.cs` (`SaMonitorLimits`, `SaMonitorBuckets`, A1/A2/A3/A4 rows + summaries, `SaDoInvoiceStates`, `SaEInvoiceActionReasons`) |
| Service | `ErpWeb.Core/Sales/SaSalesInquiryService.Monitor.cs` (the class is now `partial`; 8 methods + `GateOwnAsync` + the SQL-range helpers) |
| Interface | `ISaSalesInquiryService` — 8 new members |
| Query | `SaInquiryQuery` gained `AsOfDate`/`OverdueOnly`/`Bucket`/`InvoiceState`/`PendingOnly`/`ExpiringSoonOnly`/`ExpiredOnly` (Phase-1 methods ignore them, so no shipped screen changed) |
| Menus | `MenuCodes.SalesMonitor` + 4 leaf codes; `menus.xml` `SA_MONITOR` group (SortOrder 5); `scripts/init-sales-monitor-menu.sql` |
| Pages | `ErpWeb.UI/Sales/Monitor/SaSoAgeing`, `SaDoNotFullyInvoiced`, `SaQtExpiry`, `SaEInvoiceAction` (`.razor` + `.razor.cs`) |
| Exports | 4 routes in `ErpWeb/Sales/SaInquiryExportEndpoints.cs`, same service method + same filter, refused-not-truncated past 50 000 rows |
| Tests | `ErpWeb.Tests/SaMonitorServiceTests.cs` (50) + `SaInquiryNavigationTests.cs` (13) |

**Charts (Phase D) are not built** — §9 demotes them to a UX choice, and none of the four screens needed
one to carry its decision. Adding one later is additive (a `DxChart` beside the existing chips).

**Not done, deliberately:** Phase B (its every `Error` classification still needs the §7.2 Gate 1
evidence) and C1/C2/C3 (not started). D2/D5/D6/D8/D9 remain business sign-off items; the monitoring
thresholds already shipped are the plan's recommended values and are labelled as monitoring in the UI.

**Still owed (needs a human):** apply `scripts/init-sales-monitor-menu.sql` **twice** on scratch then once
on dev `ERPWeb`; a browser smoke of the four pages (the suite never renders a page); and a ROLE needs
`RoleMenuPermission.IsAllowed` before anyone sees the menus.

---

## Appendix A — Review received 2026-09-26 (verbatim)

> Preserved as received; headings demoted to fit the appendix. Every item is dispositioned in §0.1.

### Review: Sales Decision-Support Screens

#### Overall Assessment

**Score: 9.2 / 10**

The plan is strong and close to implementation-ready. The architecture, screen decomposition, security guardrails, performance requirements, testing strategy, and verification steps are well defined.

The main remaining risks are **business-semantic correctness**, especially in the Exception Check, e-Invoice Action Queue, Customer Analytics, and credit-exposure proxy.

**Recommendation: revise these points before giving the plan to the coding agent.**

---

#### 1. Executive Review

##### Strengths

- Clear separation between `SA_INQUIRY` row-level drill-down and the new `SA_MONITOR` decision-support screens.
- Good service/DTO/UI separation.
- Explicit tenant/access/security requirements.
- Server-side aggregation and N+1 avoidance are clearly stated.
- Read-only design is appropriate.
- Persisted quantity/status fields are explicitly protected from accidental recomputation.
- Boundary-focused tests are unusually good.
- Build, test, menu deployment, browser smoke, CSV, and authorization verification are covered.

##### Main Risks

1. Some exception rules assume that persisted quantities are directly comparable.
2. A2 wording can make partial billing look like an invoice-missing problem.
3. A4 contains monitoring rules that should be explicitly separated from transactional business rules.
4. Some B1 exception rules may generate false positives unless existing ERP invariants are verified.
5. C1 is overloaded and the "RFM-lite" terminology may imply scoring that is not actually implemented.
6. `ExposurePercent` can easily be misunderstood as actual AR credit exposure.
7. Growth from a prior value of zero needs an explicit display rule.
8. Authorization should be tested more deeply, not only browser-smoked.
9. The requirement for a chart on every screen should be treated as a UX decision rather than an architectural requirement.

---

#### 2. Required Changes

##### 2.1 Verify quantity semantics before B1

The plan compares:

- `OrderQty`
- `ShippedQty`
- `DeliveredQty`
- `InvoicedQty`
- `WrittenOffQty`

Before implementing rules such as:

- `SO_OVER_SHIPPED`
- `SO_OVER_DELIVERED`
- `SO_OVER_INVOICED`
- `SO_OVER_WRITTEN_OFF`

the implementation agent must verify from the existing ERP code/database semantics:

- UOM compatibility
- sign convention
- whether quantities are base or transaction UOM
- when each value is updated
- whether the values are line-level or aggregated
- whether partial/return/adjustment transactions affect them

**Required plan addition**

> Every persisted quantity used in an exception comparison must be verified against its existing production semantics, UOM, sign convention, and update lifecycle before the rule is implemented.
> If the semantics cannot be established, the rule must not be implemented as an `Error`.

##### 2.2 Rename A2

Current concept:

> Delivered-not-invoiced

Recommended:

> **Delivered Not Fully Invoiced**

Reason:

A DO with:

`BillingStatus = PARTIAL`

may legitimately have an invoice number while still having an uninvoiced balance.

The screen should distinguish:

- no invoice
- partially invoiced
- fully invoiced
- written off

If existing quantities already provide the information, consider displaying:

- Invoiced Qty
- Balance-to-Invoice Qty

No new database columns are required.

##### 2.3 Clarify A1 ageing terminology

The plan correctly separates:

- SO age
- delivery overdue

Make the UI terminology explicit:

- **SO Age**
- **Delivery Due Date**
- **Overdue Days**

Do not let users interpret `AgeDays` as delivery ageing.

##### 2.4 Clarify the two quotation-expiry exception rules

The plan contains:

`QT_CURRENT_EXPIRED`

and

`QT_UNSWEPT_EXPIRY`

Document their intended difference explicitly.

Recommended wording:

- `QT_CURRENT_EXPIRED`: current quotation's persisted status is already `EXPIRED`.
- `QT_UNSWEPT_EXPIRY`: current quotation's status remains `NEW/SENT`, but `ValidUntil < today`.

This makes B1 useful for detecting status/date inconsistency rather than producing duplicate-looking exceptions.

##### 2.5 Clarify D5 as a monitoring rule

The plan says:

> No invented business rules.

But D5 introduces:

> submitted pending > 3 days

This is acceptable as a **monitoring/action-queue threshold**, but it should not be described as a transactional business rule.

Recommended guardrail:

> No undocumented transactional business rules. Approved monitoring thresholds in §11 are presentation/monitoring rules only and must never modify transactional processing.

##### 2.6 Strengthen B1 exception validation

Every B1 rule should be tied to an existing production invariant or documented engine semantic.

For example:

- `INV_NONPOSITIVE_QTY`
- `CDN_NONPOSITIVE_TOTAL`
- `CDN_UNKNOWN_TYPE`

should be checked against actual document-type/sign conventions before implementation.

Recommended rule:

> An exception may be classified as `Error` only when the existing ERP transaction/posting logic establishes that the condition is invalid. Otherwise it should be omitted or downgraded to a review-only warning/info condition.

This prevents the monitor from becoming a false-positive generator.

---

#### 3. Customer Analytics Review

##### 3.1 Rename "RFM-lite"

The current fields include:

- Recency
- Frequency
- Monetary/value-related metrics

but no actual RFM score or segmentation.

Recommended screen name:

> **Customer Sales Analytics**

or:

> **Customer RFM-style Analytics**

Use "RFM" only if actual R/F/M scoring is implemented.

##### 3.2 Split C1 visually

The C1 DTO contains many fields.

Recommended UI structure:

###### KPI area

- Customer Count
- Active Customers
- New Customers
- Lapsed Customers
- Net Sales
- AOV

###### Main grid

- Customer
- Sales Rep
- Invoice Count
- Net Sales
- AOV
- Return Rate
- Growth
- Recency

###### Optional/detail area

- Frequency
- First Invoice
- Last Invoice
- Credit Limit
- Period Sales / Credit Limit

This prevents an excessively wide management grid.

##### 3.3 Define the zero-prior growth case

Current definition:

`Growth = (current - prior) / prior`

with null when prior = 0.

This is mathematically safe, but the UI needs a business-readable result.

Recommended:

- `prior > 0` → calculate Growth %
- `prior = 0 && current > 0` → Growth % = null, Trend = `New`
- `prior = 0 && current = 0` → Growth % = null, Trend = `No Activity`

Add tests for these cases.

##### 3.4 Rename ExposurePercent

Current definition:

> period NetSales / CreditLimit

The plan correctly says this is not receivable exposure.

However, a user could easily interpret "Exposure %" as actual credit-limit utilization.

Recommended label:

> **Period Sales / Credit Limit**

or:

> **Period Sales-to-Credit Limit %**

Do not label it simply as `Exposure %`.

If the business does not find this metric useful, it can also be removed until AR exists.

---

#### 4. C2 Sales Trend

C2 is well designed.

Keep:

- Day
- Month
- Year
- server-side aggregation
- prior-period comparison
- half-open date ranges

The half-open period tests are particularly important.

No major structural change is required.

---

#### 5. C3 Tax Analysis

The current screen is better described as invoice tax analysis than an SST compliance report.

Recommended name:

> **Sales Tax Analysis**

unless the underlying tax-group data is specifically confirmed to represent the required SST reporting classification.

Do not allow users to interpret this screen as an SST return/compliance report unless that scope is explicitly supported.

---

#### 6. Phase D Chart Requirement

The plan currently requires a chart on every screen.

This is reasonable for the proposed screens, but it should be treated as a UX requirement rather than an architectural rule.

Recommended wording:

> Each monitor screen should include a chart where it provides meaningful decision-support value. Charts must not be added merely to satisfy a structural requirement.

If the business explicitly wants all 8 screens to have charts, retain the current requirement.

---

#### 7. Authorization Testing

The plan already tests permission denial through browser smoke.

Add automated tests for:

- wrong Tenant
- wrong Company
- wrong Branch
- missing ACCESS
- incorrect `menuCode`
- export authorization
- tenant scope applied before permission/data retrieval

This directly validates the plan's fail-closed security requirement.

---

#### 8. D1–D9 Decision Classification

Not all D1–D9 decisions are the same type.

##### Business/product decisions

- D1 — New Monitor group
- D2 — Age buckets
- D5 — 3-day e-Invoice threshold
- D6 — Growth comparison
- D8 — Credit-limit proxy
- D9 — Tax analysis

##### Existing-system semantic verification

- D3 — PostedDate fallback
- D4 — BillingStatus definition
- D7 — Info-level exceptions

D3/D4/D7 should be validated against the existing ERP implementation rather than treated purely as preference decisions.

---

#### 9. Recommended Revised Build Gate

Before coding begins, the AI agent should perform a **semantic verification pass**.

##### Gate 1 — Existing code verification

Verify:

- SO quantity lifecycle
- DO billing status semantics
- quotation status lifecycle
- e-Invoice status/submission model
- CN/DN sign conventions
- invoice quantity semantics
- tax-group semantics
- tenant/company/branch filtering pattern
- existing analysis service result shapes

##### Gate 2 — Update plan

The agent must update the implementation plan if any assumption differs from the existing code.

##### Gate 3 — Only then implement

Do not allow the agent to invent a new field, status, calculation, or transactional rule to make the screen work.

---

#### 10. Final Assessment

##### Current plan

**9.2 / 10**

##### After the above revisions

**Expected: 9.6–9.7 / 10**

The plan is already strong enough structurally. The remaining work is primarily to remove ambiguity around existing ERP semantics and prevent the monitor screens from presenting technically calculated but misleading business information.

##### Implementation readiness

**Current:** Almost ready, but revise first.

**After revisions:** Ready for AI-agent implementation.

##### Most important areas to fix first

1. **B1 Exception Check**
2. **A4 e-Invoice Action Queue**
3. **C1 Customer Analytics / Credit Proxy**
4. **Quantity semantics verification**
5. **Authorization test coverage**

Do not expand the scope further until these are resolved.
