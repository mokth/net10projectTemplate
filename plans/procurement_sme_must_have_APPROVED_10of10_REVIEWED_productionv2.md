# Procurement SME Must-Have Enhancements — Reviewed AI Coding Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Review status:** **APPROVED FOR IMPLEMENTATION — A/B/D (10/10 design-review target, subject to build/UAT gates)**  
**Feature C:** **CONDITIONAL / NO-GO until availability-contract evidence is supplied**  
**Business scope:** Malaysian SME procurement daily operations and defensible purchasing KPIs.  
**Primary invariant:** Existing PR → PO → GR/NG → purchase invoice/CN/DN and self-billed document behaviour, numbering, posting, rollback, stock balances, inventory valuation, costing, and month-end **must not change**.

> **Approval meaning:** This is an implementation-ready *scoped design* for A/B/D. A rating of 10/10 reflects the reviewed plan's scope, controls and acceptance criteria, **not proof that future code has passed tests**. Agent must report actual verification evidence before claiming implementation approval. No code has been modified by this review.

## 1. Decision: only four useful features, no procurement redesign

| Feature | Priority | Implementation status | User value | Writes to existing transaction tables? |
|---|---|---|---|---|
| **A. Procurement Daily Workbench** | P1 | **APPROVED** | One screen for today's PR, overdue PO, due soon, matching issues | **No** |
| **B. Supplier Delivery Follow-up** | P1 | **APPROVED, additive table only** | Record supplier calls, revised promises and next action | **No** |
| **D. Procurement KPI & Supplier/Price Analysis** | P1 | **APPROVED with metric gates** | Purchase spend, outstanding, delivery and price trend | **No** |
| **C. Purchase Suggestions (advisory)** | P2 | **CONDITIONAL** | Identify potential shortages without creating PR automatically | **No** |

**Explicitly excluded:** auto-PR/PO generation, approval workflow, RFQ, supplier portal, supplier master price edits, PO ETA edits, invoice mismatch override, stock reservation/commitment creation, new stock availability engine, payment/AP aging, tax/e-Invoice processing changes, ledger/GL updates, changes to any existing transaction service posting/rollback method. **Do not implement optional features simply because a convenient field exists.**

## 2. Verified branch evidence and important corrections

### Confirmed by direct `productionv2` source inspection

- `ErpWeb.UI/Purchase/Inquiry/PoOrderOutstandingInquiry.razor.cs` calls `IPoPurchaseInquiryService.GetPoOutstandingAsync` and `GetPoOutstandingSummaryAsync`; its list already has a PO/revision navigation action.
- `ErpWeb.UI/Purchase/Inquiry/PoMatchingInquiry.razor.cs` calls `GetPoMatchingAsync` and `GetPoMatchingSummaryAsync`, with existing `ReceivedNotInvoiced` preset.
- `ErpWeb.UI/Purchase/Inquiry/PoPriceHistoryInquiry.razor.cs` calls `GetPurchasePriceHistoryAsync` and `GetPurchasePriceHistorySummaryAsync`.
- `ErpWeb.UI/Purchase/Inquiry/PoDeliveryPerformanceInquiry.razor.cs` calls `GetDeliveryPerformanceLinesAsync`, `GetDeliveryPerformanceSummaryAsync` and `GetDeliveryPerformanceReceiptsAsync`.
- `ErpWeb.Core/Purchase/IPoPurchaseInquiryService.cs` declares these existing methods; **reuse rather than duplicate the query/business semantics**.
- `ErpWeb.Core/Purchase/PoDeliveryPerformanceCalc.cs` contains `WalkReceipts`, `ResolveStatus`, lateness and completed delivery calculations.
- `ErpWeb.Core/Purchase/PoInvoiceService.cs`, `ValidateMatchOnPostAsync`, already checks net received, invoiceable quantity, purchase UOM and supplier-item price tolerance; do not change it.
- `ErpWeb.Model/Entities/Purchase/PoOrderDetail.cs` has `EtaDate` and `RecvDate`; `PoOrder` has `PoRelNo`, `FinClosed`, `Status`; `PoVendorByItem` has `OrdLevel`, `SafetyStock`, `LeadTime`, `UnitPrice`, `PackSize`, `PurUom`.
- Existing tests under `ErpWeb.Tests/Procurement/Inquiry/` and `ErpWeb.Tests/Procurement/Transaction/` provide a regression baseline.

### Corrections to previous plan

1. **Do not call current PO ETA an immutable original promise.** `PoOrderDetail.EtaDate` is an existing field; historical immutability has not been proven. The follow-up screen must label it **PO ETA (current revision)**, not **Original ETA**. Follow-up notes preserve supplier-stated dates; they do not retroactively establish an original contractual date.
2. **Do not promise a precise historical OTIF based on today's PO data.** Existing delivery-performance inquiry is authoritative for its defined *current/as-of* computation. Where revision-date history is missing, label `Based on recorded PO ETA; original-promise history unavailable`. Never backfill fictional promise dates.
3. **Do not add a second stock availability calculation.** Stock commitments/reservations are being enhanced elsewhere; Purchase Suggestions is conditional until the authoritative branch implementation is verified.
4. **Do not show a numeric MYR invoice-spend KPI** until posted invoice/CN/DN sign, currency-rate and self-billed inclusion rules are proven from executable code and sample transactions. If unproven, show **Unavailable — requires reconciliation**; do not quietly substitute a gross sum.
5. **Do not equate `Received Not Invoiced` with AP liability.** The existing matching screen explicitly labels proportional received amount as inquiry-only and not stock valuation. Display count first; any value must be marked **estimate**, not GL balance.
6. **Do not assume existing inquiry summary filters exactly match a new workbench card.** Reuse identical query/preset/as-of and validate count-to-detail reconciliation; if semantics differ, remove the card or derive both from a shared read-only query.

## 3. Non-negotiable no-impact contract

**Protected implementation areas (read-only usage permitted; business logic changes prohibited):**

- `ErpWeb.Core/Purchase/PoPrService.cs`, `PoOrderService.cs`, `PoInvoiceService.cs`, `PoCdnService.cs`, `PoSbInvoiceService.cs`, `PoSbCdnService.cs` and existing posting/rollback coordinators.
- Inventory GR/NG posting/rollback, stock balance and cost-layer services; production material issue/WO processing; all month-end, costing, GL, tax and document-numbering logic.
- Existing purchase entity mappings and transaction table schemas (PR, PO, GR/NG, invoices, CN/DN).

**Allowed modifications:** new inquiry-only services/DTOs, new `.razor`/`.razor.cs`/CSS pages, menu/authorization/DI registrations, optional small *presentation-only* action in existing delivery inquiry, a new isolated follow-up entity/config/DbSet/table and indexes, and tests. Existing inquiry services may be consumed, but changing their query predicates is **not** part of this plan. If a necessary change falls outside this allowlist, stop and request separate approval.

**Write allowlist:** Only `PoDeliveryFollowUp` may be inserted by the new features. No other table may be updated, inserted or deleted. No triggers, cascade FK, SQL job, background process or shared SaveChanges interceptor may cause secondary writes. **Purchase Suggestions and KPIs are read-only.**

**Feature switches:** New menu permissions/feature flags default disabled until validated; disabling features must restore the previous user experience. No mandatory new field on existing PR/PO/GR/invoice screens.

## 4. Mandatory Phase 0: repository and data contract preflight

Agent must produce `docs/procurement-enhancement-preflight.md` with a matrix containing **file path, method signature, DTO, exact filters, source tables, status/revision grain, time basis, security scope, tests, and verification result** for each metric. Capture current commit SHA and build baseline. Plans are not executable evidence.

Required inspections:

1. Read the four existing inquiry pages and code-behinds, `IPoPurchaseInquiryService`, implementation and Phase2, all inquiry DTOs/query definitions, `PoInquiryPageBase`, `PoInquiryNavigation`, `PoInquiryWorkbenchPresets`, menu codes, menu seeding, DI and export endpoints.
2. Determine which existing summary methods can be used unchanged; test summary counts vs paged details with the *same* filters. Identify server paging and count semantics.
3. Confirm PO revision authority, cancellation/force-close status handling, partial GR/NG receipts, negative returns, `EtaDate` null handling, timezone and as-of semantics.
4. Trace purchase invoice types and posting statuses, quantity CN vs value CDN, currency conversion and posted/rollback handling **before** enabling any spend KPI.
5. Inspect current inventory availability/commitment and demand services **after** confirming branch HEAD. Determine whether available stock already nets reservations, open sales demand or WO demand; determine incoming PO eligibility by date and UOM. Mark C `GO` only if authoritative APIs are proven.
6. Confirm `CompanyCode`, `BranchCode`, optional location authorization, user permission model, current menu registration conventions, DbContext configuration, deployment SQL script conventions, row-version behaviour and schema existence.
7. Capture existing procurement/inventory regression tests and baseline behaviour for PR, PO revisions, GR posting, purchase invoice post/rollback and credit notes.

**Preflight gate:** A/B/D may proceed only for verified contracts. C remains blocked if availability semantics are uncertain. **Never invent missing DTOs or infer stock rules from table names.**

## 5. Feature A — Procurement Daily Workbench (APPROVED)

**New UI:** `ErpWeb.UI/Purchase/Inquiry/PoProcurementWorkbench.razor` + `.razor.cs` + existing styling convention. New read-only facade `IPoProcurementWorkbenchService` / `PoProcurementWorkbenchService` only if necessary; place new DTOs alongside existing purchase inquiry DTOs after preflight.

**UX:** One Purchase > Workbench menu. Default branch = authorized current branch; date/as-of = current company-local date; date-range filtering optional per metric. Four primary actionable cards: **PR requiring purchasing action**, **Overdue PO lines**, **PO due next 7 days**, **Received not invoiced**. Optional fifth **Price mismatches** only if existing matching summary predicate reconciles. Add **Potential shortages** only after C GO. Show counts and compact rows with one-click navigation to existing entry/inquiry screens. No new entry forms.

**Definitions:**

- `PR requiring purchasing action`: use the existing PR status/remaining definition, not merely all NEW PRs. If approval/status semantics cannot identify actionable PRs, title **Open PR Lines** and use exact existing inquiry preset.
- `Overdue PO`: latest live PO revision, positive outstanding balance, eligible non-cancelled status, ETA before company-local as-of date; align with existing inquiry.
- `Due next 7 days`: outstanding eligible PO lines with ETA from as-of through as-of + 7 calendar days; distinguish inclusive/exclusive boundaries in tooltip and tests.
- `Received not invoiced`: reuse existing matching summary count, **not** an AP aging count.

**Interaction:** Click card to show matching grid on same page or navigate to an existing inquiry with verified filter parameters. If existing inquiry does not support the required deep-link filter, use an in-page read-only grid; **do not modify the existing inquiry's business query**. Provide supplier and buyer filters only where source data supports them. Reusable `CommonDataGridEx` with readable widths, horizontal scroll, resizing, server paging and CSV export via existing patterns. Loading/error/empty/unavailable states distinct. Keep click path <=2 for overdue PO. No user-entered mandatory data.

**Correctness:** Count and rows must use the same predicate, tenant, branch, as-of and revision authority. Count must equal total matching records, not current page size. Multi-branch totals only for explicitly authorized branches. Do not issue N+1 queries or unbounded full-table scans.

**Acceptance:** Existing PR/PO/GR/invoice pages and services unchanged; no writes; card totals reconcile against existing inquiries in fixture data; permissions protect cards, details, export and drill-down; responsive layout; feature flag off = no visible change.

## 6. Feature B — Supplier Delivery Follow-up (APPROVED)

**Purpose:** Let a buyer log supplier calls and next action in seconds without changing PO, GR, supplier master or inventory.

**UI:** Add small `Follow Up` action in `PoDeliveryPerformanceInquiry` (presentation only) or a separate read-only follow-up worklist. Show PO number/revision/line, supplier, **PO ETA (current revision)**, outstanding quantity, overdue days, latest follow-up, next follow-up. Popup fields: `Outcome` (`No Response`, `Confirmed`, `Delayed`, `Resolved`), `SupplierStatedDate` optional, `NextFollowUpDate` optional, `Note` short, contact timestamp default now. One click to save; default user and time automatic. `Resolved` closes the *follow-up task only*; **never closes PO**. Follow-up history visible read-only, latest first.

**Isolated schema:** `PoDeliveryFollowUp` (only if absent), fields: `Id bigint identity PK`, `CompanyCode`, `BranchCode`, `PoNo`, `PoRelNo`, `PoLineNo`, `ContactAtUtc datetime2`, `Outcome varchar(20)`, `SupplierStatedDate date NULL`, `NextFollowUpDate date NULL`, `Note nvarchar(500) NULL`, `CreatedBy`, `CreatedAtUtc datetime2`. Use repo-compatible field lengths/types; inspect PO key widths before scripting. Index `(CompanyCode, BranchCode, PoNo, PoRelNo, PoLineNo, ContactAtUtc DESC)` and due-worklist index `(CompanyCode, BranchCode, NextFollowUpDate)` if query plan warrants. **Append-only**: no ordinary-user edit/delete; avoid RowVersion unless actual update requirement exists. No cascade FK, triggers or changes to PO entity. Idempotent `IF OBJECT_ID(...) IS NULL` creation and guarded indexes; deploy separately with rollback instructions **for new objects only**.

**Security and validation:** Verify target PO line exists **within authorized company/branch**, including revision; require write permission separate from read. Server-side whitelist outcomes, validate date ranges, length and UTC conversion; store no untrusted HTML. Ignore client-provided CreatedBy/company. Follow-up insert must not call PO transaction services or alter any PO field. Historic notes remain attached to original revision; optionally show other revisions in a separate clearly labelled section.

**Metrics:** Follow-up overdue task count and last-contact age are operational measures only. Supplier-stated date **must not replace** PO ETA in delivery KPI. Historical original promise cannot be claimed unless a reliable source exists.

**Acceptance:** New follow-up saved and visible in <=30 seconds; user without write permission blocked; cross-branch access blocked; same PO before/after has byte-equivalent protected business values; PO revision, GR and invoice matching still work unchanged; no new table update outside `PoDeliveryFollowUp`.

## 7. Feature D — Procurement KPI & Supplier/Price Analysis (APPROVED WITH METRIC GATES)

**New UI:** `ErpWeb.UI/Purchase/Inquiry/PoProcurementKpi.razor` + `.razor.cs`, matching purchase inquiry conventions. **Keep six metric slots maximum; display only verified metrics**, never fabricated zero or unsupported RM values. All cards show `Definition`, `As-of/Period`, `Data freshness`, `Scope`, `Exclusions`, `View transactions`. Grid/CSV export required; charts optional.

| Metric | Approved basis | Gate / caveat |
|---|---|---|
| **1. Outstanding PO lines / value** | Existing latest-revision open PO inquiry; qty × documented PO price, with discount/tax semantics from existing PO calculator | Cross-currency RM value **disabled** until exact historical rate and calculation verified; otherwise show per currency and line count |
| **2. Overdue PO lines / value** | Existing PO outstanding + ETA/as-of | Missing ETA separate; exclude cancelled/closed per authoritative inquiry; currency gate applies |
| **3. Supplier delivery on-time %** | Existing `GetDeliveryPerformanceSummaryAsync` and `PoDeliveryPerformanceCalc` | Show numerator/denominator, missing ETA count, partial-delivery treatment; label ETA provenance accurately; **not guaranteed original-promise OTIF** |
| **4. Received not invoiced** | Existing `GetPoMatchingSummaryAsync` / `ReceivedNotInvoiced` | Count first; optional value labelled **inquiry estimate**, not AP/GL liability or inventory valuation |
| **5. Purchase price movement** | Existing posted purchase invoice price history/summary | Compare identical item, purchase UOM and currency; mixed UOM only after proven conversion; show last vs previous, not imaginary savings |
| **6. Net posted purchase invoice spend** | Posted purchase invoices and semantically eligible CN/DN, at document/line grain | **DISABLED UNTIL** invoice type/status, quantity CN, value CDN/DN, reversals, date basis, supplier, tax and MYR conversion reconciled to controlled samples; exclude self-billed unless explicitly defined |

**Supplier analysis:** Top suppliers by verified invoice-based spend (only if metric 6 enabled), delivery performance with sample size, overdue PO count and item price trend. No arbitrary supplier ranking score or savings claim. Filters: authorized branch, period, supplier, item and buyer only if the source supports each. Do not join supplier master to reconstruct historical prices. All as-of reports exclude future GR movements where applicable.

**Spend reconciliation cases:** invoice only, partial GR, partial invoice, quantity CN linked to invoice, value CDN, debit note, cancellation, rollback, mixed currency, rate changes, inclusive/exclusive tax, old PO revisions, missing supplier and no activity. Document signed arithmetic from actual repo code. If uncertain, keep metric 6 hidden/Unavailable and ship other five.

**Acceptance:** Every enabled metric is reproducible from existing inquiries or source transactions; denominator/rounding/timezone documented; export agrees with grid; multi-tenant isolation; no write operations. A metric can be withheld without blocking other approved features.

## 8. Feature C — Advisory Purchase Suggestions (CONDITIONAL / SEPARATE GO-NO-GO)

**Why gated:** Incorrect suggestions could cause over-ordering or stock shortages, especially with concurrent sales DO, inventory issue, WO material demand and stock commitment enhancements. This feature is **not** an approval to introduce or alter stock reservation logic.

**GO evidence required:** Locate the actual authoritative availability/commitment API and prove how it handles company/branch/warehouse, on-hand vs reserved, pending DO, WO material demand, future stock, lot, UOM conversion, open PO balance, revised/cancelled PO, ETA and duplicate demand. Produce 10 reconciliation examples against current inventory inquiry and open demand. **If no authoritative unified source exists, mark C NO-GO and do not build the numeric suggestion page.**

**If GO:** Read-only `PoPurchaseSuggestions.razor` + `.razor.cs`, columns item/warehouse, verified available qty, verified unfulfilled demand, eligible incoming before need date, safety stock, suggested qty, need date, source refs, confidence. `SuggestedQty` is **advisory only**, rounded only using verified UOM/pack rules. Incomplete source => `Needs review` and **no numeric suggested quantity**. Only action `Open existing PR Entry` (no prefilled hidden save, no document generation). Clearly state snapshot time and `Stock may have changed`. Default hide zero suggestions. No new tables and no inventory writes.

**Mandatory tests:** simultaneous demand, reserved stock, double-counting WO/DO, future-dated receipt, multiple warehouses, revised/cancelled PO, partial GR, negative/zero balances, mixed UOM, pack rounding, stale snapshot, branch isolation. If any fails, feature stays disabled.

## 9. Implementation map and change allowlist

| Component | Allowed files / patterns | Restrictions |
|---|---|---|
| UI | New `PoProcurementWorkbench`, `PoProcurementKpi`, conditional `PoPurchaseSuggestions` `.razor/.razor.cs` and CSS | Match existing `iv-page`/`CommonDataGridEx`; no existing transaction entry redesign |
| Follow-up UI | Small action/popup on existing `PoDeliveryPerformanceInquiry` or new worklist | UI-only integration; no PO update |
| Services | New read-only facade and isolated `PoDeliveryFollowUpService` + interfaces | Existing purchase/stock posting services unchanged |
| DTOs | New purchase inquiry models/DTOs at verified existing location | No changes to core transaction DTO validation |
| Data | New follow-up entity/config/DbSet and additive SQL script only | No existing transaction table alteration; no triggers/cascade |
| Registration | Existing DI, menu triad, permissions and export endpoint conventions | Default disabled until validated; no access without authorization |
| Tests | New inquiry/follow-up/KPI tests | Existing procurement/inventory tests must pass unchanged |

**DevExpress binding rule:** New `DxTextBox`/`DxDateEdit`/other validated editors using `Value`/`Text` with change callbacks must supply matching `ValueExpression`/`TextExpression`, or use supported `@bind-*`; follow the repository's working editor patterns. Never suppress validation globally to hide missing expression exceptions.

**Performance:** Paginate and filter in SQL; bounded dates for history; no full-table materialization or N+1 per supplier. Count and detail use same query basis. Cancellation tokens. No slow work in component initialization that blocks the UI indefinitely. Document index usage and validate typical SME dataset plus large dataset.

## 10. Test matrix and non-regression gates

### Baseline before any code

Record git SHA, `dotnet build` outcome, procurement/inventory test outcome, current inquiry counts, representative PR/PO/GR/invoice sample IDs, posted stock and cost balances. Compare **before/after** in an isolated test database, never production.

### Automated tests (minimum)

1. **Workbench:** latest PO revision only, cancelled PO excluded, PR partial consumption, date boundaries, summary count = detail total, filters and export, branch/tenant isolation.
2. **Follow-up:** append-only insert, outcome/date validation, missing/unauthorized PO, cross-branch isolation, old-revision notes, concurrent follow-ups, no PO/inventory side effects.
3. **KPI:** partial receipts, no ETA, late receipt, GR/NG net effects, returns, rolled-back invoice, CN/DN sign, mixed currency, UOM changes, zero denominator, as-of future receipt exclusion, missing data => Unavailable.
4. **Suggestions (only GO):** verified availability/commitment cases and zero writes; stale data warning.
5. **Existing:** all `ErpWeb.Tests/Procurement/Inquiry/*`, `ErpWeb.Tests/Procurement/Transaction/*` and affected inventory/posting/costing regression suites unchanged.

### Manual UAT (Malaysian SME scenarios)

- Buyer opens Workbench and reaches overdue PO in <=2 clicks; count agrees with outstanding inquiry.
- Buyer logs supplier delay/next call in <=30 seconds without changing PO ETA or GR balances.
- Manager views supplier delivery and price movement and exports a reconciled report.
- Missing ETA, missing exchange rate, incomplete stock data and unavailable spend semantics show **clear warnings, never false zeros**.
- Existing PR entry, PO revision, GR/NG posting, invoice post/rollback and credit notes behave identically; compare transaction state, stock balance, valuation and month-end outputs.
- Role-based access tested for buyer, manager, read-only user and unauthorized branch; export and deep-link protected.

### Stop-ship conditions

Any changed result in existing procurement posting, rollback, document numbering, stock commitment, stock balance, cost layer, month-end, invoice matching enforcement, tax, supplier master price, cross-tenant data access, or misleading KPI. Agent must revert offending changes and report a blocker rather than bypass controls.

## 11. Exact agent delivery order

**Phase 0 (required):** Produce verified source/contract matrix, baseline SHA/build/tests, per-KPI definition and GO/NO-GO. Do not code until completed.

**Phase 1 (APPROVED):** Workbench using existing inquiry services. Compile, test, screenshots, counts reconciliation. No schema change.

**Phase 2 (APPROVED):** Delivery follow-up table/service and minimal UI. Separate idempotent SQL deployment; prove no core-table changes. Compile, test, screenshots.

**Phase 3 (APPROVED):** KPI dashboard with individually gated cards. Reconcile actual source values; hide any unverified card. Compile, test, screenshots/export.

**Phase 4 (CONDITIONAL):** Advisory suggestions **only** if Phase 0 authoritative stock/demand evidence passes; otherwise record `NO-GO` and close release without C.

**Each phase's evidence bundle:** changed-file list, SQL script, compile result, test results, before/after protected-data checks, permission/tenant tests, screenshots, unresolved issues, feature-flag rollback instructions. Do not claim success from source inspection alone.

## 12. Final independent review and approval

**Scope fitness for Malaysian SME: 10/10 design target.** Four practical features; no unnecessary supplier portal, RFQ or approval engine. Most user interaction is a new page or optional follow-up popup.

**Non-invasive design: 10/10 design target.** Explicit write allowlist, protected services, independent menu permissions, additive follow-up table, read-only KPI/suggestions, stop-ship regression gates.

**Data accuracy: 10/10 design target conditional on evidence.** Strict per-metric enablement, no invented original ETA, no false AP liabilities or MYR conversions, and no independent stock availability formula.

**Final decision:** **APPROVED FOR AI CODING AGENT IMPLEMENTATION — Features A, B and D only, with mandatory preflight and validation gates.** Feature C remains **CONDITIONALLY APPROVED / BLOCKED FOR CODING** until authoritative stock availability and demand inputs are verified. Existing procurement logic and entry screens are explicitly protected. **No claim is made that the unimplemented code is already tested or production-approved.**
