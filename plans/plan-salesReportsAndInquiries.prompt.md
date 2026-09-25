# Plan: Sales Module Reports & Inquiries

## TL;DR

Build a read-only Sales Inquiry layer on top of the existing entities, reusing the `ISaSalesAnalysisService` / `SaAnalysisPageBase` / export-endpoint / menu-triad patterns. Most "MUST" reports are data-ready now; item/category/warehouse analysis is Phase 2; AR-dependent and margin reports are Phase 4, gated on a new AR module and a COGS snapshot.

---

## Scope decisions (locked)

- **Tenancy** = `CompanyCode` (+ `BranchCode` where a table carries it). There is **no `TenantId`** in this solution. Every query filters company/branch; never invent a tenant key.
- **Query pattern** = LINQ/EF over entities via repositories/services — **no SQL views**. Add a SQL `scripts/*.sql` only for an index when a query profile demands it.
- **AR / payments do not exist** (only `SaPaymentTerm`, the terms master). Statement, outstanding, aging, payment-status and AR charts are **not buildable** until an AR module lands.
- **No historical COGS** on document lines (`IvStockMaster.PurchasePrice` is current cost only). Margin/profitability/contribution are Phase 4, gated on a COGS snapshot.
- **Reuse, don't rebuild**: Sales by Customer/Date/Salesperson/Trend = existing `SaSalesSummary`; target-vs-actual = `SaSalesRepAttainment`; QT win/loss = `SaQtConversionAnalysis`; document flow = `SaDocFlowQuery` + `SaDocFlowPanel`.

---

## Implementation Guardrails (binding)

1. Do not invent database columns, statuses, formulas, tenant fields, or business rules — use what the code already defines.
2. Reuse existing services, status enums, posting rules, and CN/DN amount/sign conventions.
3. All queries stay server-side `IQueryable` until the final materialization (`SumAsync` / `CountAsync` / `ToListAsync` at the end). No client-side grouping of large sets.
4. No N+1 database calls; no per-row lookups; batched lookups only.
5. Preserve `CompanyCode` / `BranchCode` isolation in every query and every master lookup (never assume `ICode` is globally unique).
6. Read-only: never modify posted transaction data just to make an inquiry work.
7. Export uses the same service + filter + authorization as the grid.
8. Current e-Invoice status = latest authoritative submission record; history preserves all attempts.
9. Any new index must be justified by an observed query plan/profile; the only planned candidate is the customer-transaction union.
10. Do not expose AR or historical-profitability figures until their prerequisites exist.

---

## Report Status Rules (centralized — use these exact enums)

Status vocabulary lives in `ErpWeb.Core/Sales` (`SaQtCalc.cs`, `SaSoCalc.cs`, `SaDoCalc.cs`, `SaInvoiceCalc.cs`, `SaCdnCalc.cs`). Use the constants, never string literals.

| Report | Included | Excluded |
|---|---|---|
| Customer Transaction | all statuses of QT/SO/DO/INV/CN/DN (it is a movement log); show `Status` and let the user filter | none |
| Customer Sales History | `SaInvoiceStatuses.Posted` only; CN/DN netting uses `SaCdnStatuses.Posted` | NEW invoices, draft CN/DN |
| QT Status/Expiry | `IsCurrent` rows; `SaQtStatuses.Live` | SUPERSEDED (reachable via revision history only) |
| SO Outstanding/Backorder | current (`IsCurrent`) rows; `BalanceQty > 0`; `WrittenOffQty` shown but never outstanding | SUPERSEDED |
| DO Status / Pending invoice | all DO rows, `Status` shown | none |
| Invoice vs DO/SO | all invoice rows (relationship view) | none |
| CN/DN Inquiry | all rows by `SaCdnTypes` (`CN`/`DN`) | none |
| e-Invoice Status/Reconciliation | all rows with any `IRBM*` value; "not submitted" (no row / blank status) shown distinctly from "unknown" | none |

Draft documents (`NEW`) appear in the *operational* lists (transaction/status/outstanding) but never in *sales totals* (history/summary) — that POSTED-only boundary already exists in `SaSalesAnalysisService` and must be copied exactly.

**Cancellation/void mapping** (no `VOID` status exists in these tables):
- QT cancellation = `SaQtStatuses.Cancelled`.
- SO cancellation = `SaSoStatuses.Closed` with `ClosedReason = SaSoClosedReasons.ForceClosed` (or `FullyConsumed`); `ShippedQty`/`DeliveredQty`/`InvoicedQty` record what actually happened before closure.
- DO / INV / CN / DN have no cancellation status — only NEW / POSTED (+ `CLOSED` on DO).

---

## Quantity & relationship semantics (locked)

- **SO outstanding** = persisted `SaSoDetail.BalanceQty` (maintained by `SaSoQty`: `OrderQty − DeliveredQty` on order change, `OrderQty − ShippedQty` on consume/reverse). Use it as-is — do NOT recompute.
- `ShippedQty` = allocated/consumed to DO; `DeliveredQty` = delivered; `InvoicedQty` = invoiced; `WrittenOffQty` = irreversible force-close write-off (never outstanding).
- Header `FulfillmentStatus` / `BillingStatus` (`NONE` / `PARTIAL` / `FULL`) are authoritative rollups — reuse, don't re-derive.
- One SO line may feed multiple DOs; one DO may feed multiple invoices; delivery and invoicing are independent axes (see `SaDocApplication`). Relationship reports read the allocation ledger, never re-derive links from quantities.

## CN/DN sign & netting (locked)

- CN and DN are stored **POSITIVE**; `SaCdnTypes` (`CN` / `DN`) is the sign authority.
- Net sales = `Invoice + DN − CN`, netted exactly once — this is `SaSalesAnalysisService.BuildPeriodTotalsAsync` semantics. Reuse it; do not create a second convention.
- Sales history uses the same header `TotAmnt` totals, never line re-totalling. Add tests for: invoice only, invoice+CN, invoice+DN, multiple CNs, and a zero/negative net result.

## e-Invoice latest-submission rules (locked)

- `EInvDocSubmission` is one row per accepted document per submission; a re-submission adds a row. **Current status** = the highest `ID` row for `(companyID, documentType, documentNo)`, then `EInvoiceStatuses.Normalize` (legacy rows are title-case).
- **History** = all attempts for that key, chronological.
- **Reconciliation** = compare the ERP document's `IRBM*` fields against the latest authoritative submission row only — never join all rows (that duplicates invoices).
- UI distinguishes "not submitted" from "unknown/unmapped" status; test null / empty / legacy casing / unknown / valid / cancelled / submitted-pending.

## Dashboard KPI definitions (locked)

- **Sales Today/Month/Year** = three `GetSalesSummaryAsync` date windows (POSTED invoices only).
- **Open QT** = `IsCurrent` and `SaQtStatuses` ∈ {NEW, SENT, ACCEPTED}.
- **Open SO** = current (`IsCurrent`) SOs not `CLOSED`, reported as three separate chips: count, original value (header `TotAmnt`), outstanding value (original − invoiced value via `SaInvoiceDetail.SoNo` links). Never double-counted with a DO.
- **Pending Delivery** = SO outstanding quantity not yet covered by a DO = `SUM(BalanceQty)` over open SOs. Do NOT add open DO quantity on top (double-counts the same obligation). Show "Open DO quantity" as a separate chip if wanted.
- **CN/DN totals** = `SaCdn` POSTED, stored positive, grouped by `SaCdnTypes`.

---

## Phase 0 — Discovery / Contract Validation (do first; no production code)

Before Phase 1, verify against the live code and produce a short findings note (no code changes):

1. Confirm every entity/field named in this plan exists (header/detail entities above are already verified).
2. Confirm the exact status enum values in `Sa*Calc.cs` and that the Status Rules table above matches.
3. Confirm CN/DN sign semantics by re-reading `BuildPeriodTotalsAsync` and the `SaCdnCalc` fingerprint/total code.
4. Confirm SO/DO/invoice quantity maintenance (`SaSoQty`, `ISaDocApplication.Allocate*` / `Reverse*`).
5. Confirm `EInvDocSubmission` key/cardinality and `EInvoiceStatuses.Normalize`.
6. Confirm the export CSV helper + row-cap convention and the menu-triad files (`MenuCodes.cs` + `menus.xml` + `init-sales-*-menu.sql`).

**Gate:** Phase 1 starts only when the findings note signs off all six items.

---

## Phase 1 — Operational inquiries (data-ready; highest value)

These are per-document read-only grids over entities that already carry the fields. New service + DTO + pages + export + menus.

**Steps (run 1.1 and 1.2 in parallel; 1.3–1.6 depend on 1.1–1.2)**

### 1.1 New DTOs

`ErpWeb.Core/Sales/SaInquiryResults.cs`: `SaCustomerTransactionRow`, `SaCustomerSalesHistoryRow`, `SaQtStatusRow`, `SaSoOutstandingRow`, `SaDoStatusRow`, `SaDocumentRelationshipRow` (SO↔DO, DO↔INV, INV↔DO, INV↔SO), `SaCdnInquiryRow`, `SaEInvoiceStatusRow`. One shared `SaInquiryQuery` filter DTO (date range, `CustCode`, `SalesmanCode`, `BranchCode`, status) mirroring `SaSalesAnalysisQuery` in shape and half-open date handling.

### 1.2 New service

`ErpWeb.Core/Sales/ISaSalesInquiryService.cs` + `SaSalesInquiryService.cs`. Constructor `(IDbContextFactory<AppDbContext>, IInventoryTenantContext, IAccessRightService)` exactly like `SaSalesAnalysisService`. **Every method takes a `menuCode`** (the `IvStockCountService.Variance` precedent) so each screen has its own grant. Gate via `_accessRights.CanAsync(menuCode, PermissionCodes.Access, ct)`; company/branch from `_tenant.TryBranchScope()`. Read-only — never writes.

- `GetCustomerTransactionsAsync` — union `SaQt` / `SaSo` / `SaDo` / `SaInvoice` / `SaCdn` (each already carries `CustCode`) into one row shape with a `DocType` discriminator; server-side paging.
- `GetCustomerSalesHistoryAsync` — POSTED `SaInvoice` (+ CN/DN netting like `BuildPeriodTotalsAsync`) grouped by month/period.
- `GetQtStatusAsync` — from `SaQt`: `ValidUntil` (expired = `Today > ValidUntil`), `Status`, `ConversionStatus`, `CustRel` / `IsCurrent`.
- `GetSoOutstandingAsync` — from `SaSo` / `SaSoDetail`: `OrderQty` vs `ShippedQty` / `DeliveredQty` / `InvoicedQty`, `BalanceQty`, `WrittenOffQty`, `FulfillmentStatus`, `BillingStatus`. This single query answers Open SO, Outstanding Quantity, Delivery Status, Invoice Status, and Backorder.
- `GetDoStatusAsync` — from `SaDo` / `SaDoDetail`: `BillingStatus`, `InvNo`, plus `SaDocApplication` for SO↔DO and pending-invoice-from-DO.
- `GetDocumentRelationshipAsync` — SO↔DO, DO↔INV, INV↔SO via `SaDocApplication` + `SaInvoiceDetail.DoNo` / `SoNo`; reuse the `SaDocFlowQuery` enrich-style batched lookups.
- `GetCdnInquiryAsync` — from `SaCdn` filtered by `Type` CN/DN, `InvNo`, `SalesRep`, reason (`RefNo` / `Remarks`).
- `GetEInvoiceStatusAsync` / `GetEInvoiceSubmissionHistoryAsync` / `GetEInvoiceReconciliationAsync` — from `SaInvoice` / `SaCdn` `IRBM*` columns + `EInvDocSubmission` (join on `companyID + documentType + documentNo`), reusing `EInvoiceStatuses.Normalize` for legacy title-case rows.

### 1.3 New UI pages

`ErpWeb.UI/Sales/Inquiry/` (new folder). Each page `@inherits` a new `SaInquiryPageBase` cloned from `SaAnalysisPageBase` (date defaults, `HasRun`, `BuildExportUrl`). Use `CommonDataGridEx` + a `GridCustomDataSource` (the list-page pattern) or the read-only DxGrid pattern the Analysis pages use. Pages: Customer Transaction / History (one page, two tabs), Quotation Status/Expiry, Sales Order Outstanding, Delivery Status, Invoice-vs-DO/SO, CN/DN Inquiry (combined, `Type` filter), e-Invoice Status/Reconciliation.

### 1.4 Export

`ErpWeb/Sales/SaInquiryExportEndpoints.cs`, registered in `Program.cs` beside `MapSaAnalysisExportEndpoints`. Each handler calls the **same** service method as its grid (R9 parity), `[AsParameters]` query binding, CSV via the existing `Csv(...)` helper. Gate inside the service so export can't widen access. Export writes **all filtered rows** (never just the current grid page) and reuses the existing row-cap convention (`IvInquiryExportWorkbook.MaxExportRows = 50_000`); stream/iterate server-side without loading an unbounded set into memory.

### 1.5 Menus (triad, all three required)

- `MenuCodes.cs` constants under `SA_ANALYSIS` / `SA_INQUIRY`: `SA_CUST_TRX` (one menu, two tabs — transaction + history), `SA_QT_STATUS`, `SA_SO_OUTSTANDING`, `SA_DO_STATUS`, `SA_INV_VS_DOC`, `SA_CDN_INQUIRY` (one combined CN/DN screen, `Type` filter), `SA_EINV_INQUIRY` (ACCESS only; EXPORT not seeded — the CSV is gated by the same ACCESS, per the `init-sales-analysis-menu.sql` comment).
- `ErpWeb/Menus/menus.xml` rows (parent `SALES`, new `SA_INQUIRY` parent route-less group, SortOrder after Analysis).
- `scripts/init-sales-inquiry-menu.sql` (idempotent, guarded inserts, apply twice on scratch before dev).

### 1.6 Tests

`ErpWeb.Tests/SaSalesInquiryServiceTests.cs` with `[Trait(TestCategories.Name, TestCategories.Sales)]` + a screen trait (`SalesInquiry`). Cover: tenant/branch isolation, date half-open, POSTED-only where relevant, union ordering, menu gating (denied caller refused), and `EInvoiceStatuses.Normalize` on legacy rows. Register `SaSalesInquiryService` in `CoreServiceCollectionExtensions.cs`.

---

## Phase 2 — Item / Category / Warehouse analysis (extend the analysis layer)

Detail-level aggregation; the existing `SaSalesSummary` is header-only and lacks Item/Category/Warehouse.

**Steps**

### 2.1

Add `SaSalesDetailDimension { Item, Category, Warehouse }` and `SaSalesDetailRow` (Qty, Amount, NetAmount, TaxAmount, Discount, ASP) to `SaSalesAnalysisResults.cs`. Sources: `SaInvoiceDetail` (`ICode`, `FrWarehouse`, `Classification`, `ItemDiscount*` / `ItemDiscAmount*`, `TaxAmt`, `NetAmount`, `UnitPrice`, `Qty`); category = `IvStockMaster.IClass` join; warehouse from detail `FrWarehouse`. **All aggregation stays `IQueryable` and executes as SQL `GROUP BY` — never `ToList()` then group in C#.** Item/category lookups use the existing inventory lookup/context rules (`IIvInventoryLookupService`); never assume `ICode` is globally unique — scope by `CompanyCode` / `BranchCode`.

### 2.2

Extend `ISaSalesAnalysisService` with `GetSalesDetailAsync(query, dimension)` (or three named methods) implemented in `SaSalesAnalysisService` (same gating, `PostedInvoices` joined to details). Item/category/warehouse filters via `IIvInventoryLookupService` (ungated) for the pickers — reuse, don't gate. The `IvStockMaster` category join is on `(CompanyCode, ICode)` (same tenant scope as the invoice); an item with no master row renders category as "(unknown)", never a cross-company match.

### 2.3

Pages in `ErpWeb.UI/Sales/Analysis/` (`SaSalesByItem`, `SaSalesByCategory`, `SaSalesByWarehouse`) inheriting `SaAnalysisPageBase`; extend `SaAnalysisExportEndpoints` with matching CSV routes.

### 2.4

Menus: `SA_SALES_ITEM` / `SA_SALES_CATEGORY` / `SA_SALES_WAREHOUSE` + menus.xml + extend the init script. Tests in a `SaSalesAnalysisServiceTests`-style class (SQLite, seeded invoices + details).

---

## Phase 3 — Sales Dashboard

**Steps**

### 3.1

New `ISaSalesDashboardService` in `ErpWeb.Core/Sales` returning a single `SaDashboardResult`: KPI chips exactly per the locked "Dashboard KPI definitions" above (sales today/month/year; open QT; open SO count + original value + outstanding value; pending delivery = SO `BalanceQty` not yet covered by DO; CN/DN totals) + chart payloads reusing `SaSalesSummaryResult.Rows` for by-customer/by-salesperson and the Phase-2 detail rows for by-category/top-items. One bounded query per aggregate — **no per-row or per-customer/per-item database calls** (no N+1).

### 3.2

One page `ErpWeb.UI/Sales/Dashboard/SaDashboard.razor(.cs)` — KPI cards + DevExpress charts; no new framework. Menu `SA_DASHBOARD` + menus.xml + init script.

### 3.3

Outstanding/Overdue AR charts are **omitted** (AR dependency) — render placeholders or omit, per decision below.

---

## Phase 4 — Gated (do not start until dependencies land)

### 4.1 AR module prerequisite

→ then: Customer Statement, Customer Outstanding, Invoice Outstanding, Invoice Aging, Invoice Payment Status, CN/DN Applied/Outstanding, Outstanding/Overdue AR. Data hooks already present: `SaInvoice.DueDate`, `SaCust.CreditTerm` / `CreditLimit` / `AgingType` / `OpeningAmount`.

### 4.2 COGS snapshot on `SaInvoiceDetail`

(and/or `SaCdnDetail`) → then: Gross Margin, Customer Profitability, Product Contribution, as historical-cost reports.

**Preceded by a separate COGS design task** (before any code) that pins: snapshot timing (at post), cost source per line, CN/DN cost treatment, DN cost, multi-warehouse/lot cost, currency, rounding, and the immutability rule — **once a document is posted, its historical cost must not change when current inventory cost changes.** Do not start Phase 4.2 until this design is signed off.

---

## Relevant files (reuse as templates)

- `ErpWeb.Core/Sales/SaSalesAnalysisService.cs` — gate/date-range/aggregation template (`GateAsync`, `ResolveRange`, `BuildPeriodTotalsAsync`, `Money`).
- `ErpWeb.Core/Sales/SaSalesAnalysisResults.cs` — DTO/enum shape to extend.
- `ErpWeb.Core/Sales/SaDocFlowQuery.cs` + `ErpWeb.UI/Sales/Transactions/SaDocFlowPanel.razor` — already-built document-flow inquiry; reference for `SaDocApplication` joins.
- `ErpWeb.UI/Sales/Analysis/SaAnalysisPageBase.cs` + `SaSalesSummary.razor(.cs)` — inquiry-page plumbing template.
- `ErpWeb/Sales/SaAnalysisExportEndpoints.cs` — export pattern (same service method, `[AsParameters]`, `Csv`).
- `ErpWeb.Core/Menus/MenuCodes.cs` — add constants here (lines ~135–152).
- `ErpWeb/Menus/menus.xml` + `scripts/init-sales-analysis-menu.sql` — menu-triad template (both must change; `MenuDeploymentParityTests` guards the XML).
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — DI registration.
- `ErpWeb.Tests/TestCategories.cs` — trait for new test classes.

---

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 errors (Razor pages are only compiled by this, not by tests).
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"` → **the existing known-failure set is unchanged and no new failures are introduced** (at the time of writing: 9 `SaCustServiceTests` + 4 `PoSupplierServiceTests` + 2 library `InvoiceTypeCode`; treat the count as documentation, not the acceptance criterion). All new inquiry/analysis tests green.
3. Apply each new `init-sales-*.sql` **twice on a scratch DB** (second run clean no-op), then once on dev `ERPWeb`; confirm ACTIVE `MenuPermission` rows and that `menus.xml` parity passes.
4. Browser smoke: each new page filters/renders with date + customer + salesman; the CSV export matches the grid (R9); a permission-denied user reaches the unauthorized screen for the page and gets a problem/403 from the export endpoint.

### Required Phase-1 test matrix

- **Security**: access allowed/denied; company isolation; branch isolation; export endpoint gated.
- **Dates**: same-day, month, and year ranges; end-date boundary; date-only (timezone) behaviour.
- **Documents**: draft (`NEW`), posted, partial, cancelled (QT `CANCELLED`; SO `CLOSED` + `FORCE_CLOSED`), superseded revision, no related document.
- **Relationships**: SO → multiple DOs; DO → multiple invoices; invoice → multiple DO/SO refs; CN/DN → invoice; quotation → multiple revisions.
- **e-Invoice**: no submission, submitted, rejected, resubmitted, valid, cancelled, multiple attempts; legacy title-case plus null/empty/unknown status via `EInvoiceStatuses.Normalize`.
- **Empty data**: each screen renders correctly with 0 rows, 1 row, and a large result set.

---

## Decisions (locked — resolved by review)

- Read-only inquiries get **ACCESS-only** menus, CSV gated by the same ACCESS (matches the shipped analysis screens).
- New screens live under a new `SA_INQUIRY` menu group; item/category/warehouse extend the existing `SA_ANALYSIS` group.
- **Customer Transaction & History = ONE page, two tabs, ONE menu** (`SA_CUST_TRX`). Backend DTOs/services stay separate.
- **CN/DN = ONE combined inquiry** (`SA_CDN_INQUIRY`) with a `Type` filter — no separate CN and DN menus.

---

## Further Considerations

1. **Dashboard AR tiles** — omit them now (recommended), or show "requires AR module" placeholders. Recommend: omit.
2. **Margin basis** — report current-cost margin now vs. block until a COGS snapshot exists. Recommend: block (historical COGS is the honest number).

---

## Report → Phase assignment

| Report (from audit matrix) | Phase |
|---|---|
| Customer Profile | existing (`SaCustEntry`) |
| Customer Credit Limit Inquiry | Phase 1 |
| Customer Transaction / History | Phase 1 (one page, two tabs) |
| QT Status/Expiry; SO Outstanding/Backorder; DO Status; Invoice-vs-DO/SO; CN/DN; e-Invoice Status/History/Reconciliation | Phase 1 |
| Sales by Item/Category/Warehouse; Item/Customer ranking; Discount; SST-Tax; ASP; Customer Trend/Frequency/AOV/Return; QT/SO/DO "by" variants | Phase 2 |
| Sales Dashboard | Phase 3 |
| Statement / Outstanding / Aging / Payment Status / AR | Phase 4.1 (after AR module) |
| Gross Margin / Profitability / Contribution | Phase 4.2 (after COGS design) |
| Sales Document Flow Inquiry | already implemented |

---

## Appendix — Audit matrix (per-report entity/column sources)

### A. Customer

| Report | Feasibility | Source |
|---|---|---|
| Customer Profile | READY | `SaCust` + `SaCustAdd` / `SaCustContact` |
| Customer Transaction Inquiry | NEW LOGIC + NEW UI | union `SaQt/SaSo/SaDo/SaInvoice/SaCdn` on `CustCode` |
| Customer Sales History | NEW LOGIC + NEW UI | POSTED `SaInvoice` (+ CN/DN netting) |
| Customer Statement | NOT POSSIBLE YET (AR) | no receipt/AR ledger |
| Customer Outstanding | NOT POSSIBLE YET (AR) | no receipt/AR ledger |
| Customer Credit Limit Inquiry | REUSABLE | `SaCust.CreditLimit` vs invoice totals |
| Sales Trend / Frequency / AOV / Return | NEW LOGIC + NEW UI | `SaInvoice` / `SaCdn` |
| Customer Profitability | NOT POSSIBLE YET (COGS) | no historical line cost |

### B. Quotation

| Report | Feasibility | Source |
|---|---|---|
| Listing / Detail / Revision History | READY | `SaQt` / `SaQtDetail` (`CustRel`, `IsCurrent`) |
| Conversion Inquiry / Win-Loss / Rate | READY | `SaQtConversionAnalysis` |
| Status / Expiry | NEW LOGIC + NEW UI | `SaQt.Status`, `ValidUntil`, `ConversionStatus` |
| By Customer / Salesperson / Aging / Trend | NEW LOGIC + NEW UI | `SaQt` |

### C. Sales Order

| Report | Feasibility | Source |
|---|---|---|
| Listing | READY | `SaSoList` |
| Open / Outstanding Qty / Backorder | NEW UI over existing columns | `SaSo` + `SaSoDetail.OrderQty/BalanceQty/ShippedQty` |
| Delivery Status / Invoice Status | NEW UI over existing columns | `SaSoDetail.DeliveredQty/InvoicedQty`, `FulfillmentStatus/BillingStatus` |
| By Customer / Salesperson / Item | NEW LOGIC + NEW UI | `SaSo` / `SaSoDetail.ICode` |
| Aging / Expected Delivery / Cancelled / Fulfillment | NEW LOGIC + NEW UI | `SaSo` / `SaSoDetail.DeliveryDate/Eta/Etd` |

### D. Delivery Order

| Report | Feasibility | Source |
|---|---|---|
| Listing / Open / Status | REUSABLE | `SaDoList`, `SaDo.Status/BillingStatus` |
| By Customer / Item / Warehouse / Salesperson | NEW LOGIC + NEW UI | `SaDo` / `SaDoDetail.ICode/FrWarehouse` |
| SO vs DO / DO vs Invoice / Pending Invoice | NEW LOGIC + NEW UI | `SaDocApplication` + `SaDoDetail.SoNo/InvNo` |
| Partial Delivery / Performance | NEW LOGIC + NEW UI | `SaSoDetail` vs `SaDoDetail` qty |

### E. Sales Invoice

| Report | Feasibility | Source |
|---|---|---|
| Listing / Detail | READY | `SaInvoiceList` / `SaInvoice` |
| By Customer / Salesperson / Date / Trend / Ranking | READY | `SaSalesSummary` |
| By Item / Category / Warehouse | NEW LOGIC + NEW UI (Phase 2) | `SaInvoiceDetail.ICode/FrWarehouse/Classification` + `IvStockMaster.IClass` |
| Discount / SST-Tax / ASP | NEW LOGIC + NEW UI | `SaInvoiceDetail.ItemDiscount*/TaxAmt/NetAmount/UnitPrice` |
| Invoice vs DO / Invoice vs SO | NEW LOGIC + NEW UI | `SaDocApplication` + `SaInvoiceDetail.DoNo/SoNo` |
| Outstanding / Aging / Payment Status | NOT POSSIBLE YET (AR) | no receipt/AR ledger |
| Gross/Net Margin | NOT POSSIBLE YET (COGS) | no historical line cost |

### F. Credit Note / G. Debit Note

| Report | Feasibility | Source |
|---|---|---|
| Listing / Detail | READY | `SaCdnList` / `SaCdnDebitList` (`Type` CN/DN) |
| By Customer / Reason / Trend / % of Sales | NEW LOGIC + NEW UI | `SaCdn` (`InvNo`, `RefNo`, `TotAmnt`) |
| vs Invoice | NEW LOGIC + NEW UI | `SaCdn.InvNo` |
| Applied/Outstanding | NOT POSSIBLE YET (AR) | no receipt/AR ledger |

### H. Malaysia e-Invoice

| Report | Feasibility | Source |
|---|---|---|
| Status / Submission / UUID / Doc Type / History / Cancellation | REUSABLE (new UI) | `SaInvoice`/`SaCdn` `IRBM*` + `EInvDocSubmission` |
| Error/Exception | REUSABLE (new UI) | `SaEInvoiceLog` + `IrbmError`/`IrbmOutcome` |
| By Customer / Date | REUSABLE (new UI) | `EInvDocSubmission` |
| Reconciliation | NEW LOGIC + NEW UI | `SaInvoice`/`SaCdn` join `EInvDocSubmission` (`companyID + documentType + documentNo`) |
| Dashboard / Trend / Exception Aging | NEW LOGIC + NEW UI | above sources |

### I. Sales Analysis

| Report | Feasibility | Source |
|---|---|---|
| Summary / Trend / by Customer / Salesperson | READY | `SaSalesSummary` |
| Target vs Actual / Salesperson Performance | READY | `SaSalesRepAttainment` |
| by Item / Category / Warehouse | NEW LOGIC + NEW UI (Phase 2) | `SaInvoiceDetail` |
| Item/Customer Ranking, Discount, Price | NEW LOGIC + NEW UI | `SaInvoiceDetail` |
| Margin / Profitability / Contribution | NOT POSSIBLE YET (COGS) | no historical line cost |

### J. Sales Dashboard

| Item | Feasibility | Source |
|---|---|---|
| Sales Today / Month / Year | REUSABLE | `GetSalesSummaryAsync` date windows |
| Open Quotation / Open Sales Order / Pending Delivery | NEW LOGIC + NEW UI | `SaQt` / `SaSo` / `SaDo` |
| Credit Note / Debit Note | REUSABLE | `SaCdn` |
| Outstanding / Overdue Invoice / AR | NOT POSSIBLE YET (AR) | no receipt/AR ledger |
| Monthly Trend / by Customer / Salesperson / QT Conversion | READY | existing analysis |
| by Category / Top Customers / Top Items | NEW LOGIC + NEW UI | `SaInvoiceDetail` + Phase 2 |

### K. Sales Document Flow

| Item | Feasibility | Source |
|---|---|---|
| Sales Document Flow Inquiry | **ALREADY IMPLEMENTED** | `SaDocFlowQuery` + `SaDocFlowPanel` |
