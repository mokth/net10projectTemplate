# productionv2 — Sales Pricing Post-Implementation **Safe Fix** Agent Plan v1.0
**Based on actual implementation review of HEAD `1b2d70618ce44d5db0f13ea14d78697dbc27ea5f`**  
**Branch of truth:** `mokth/net10projectTemplate` → `productionv2`  
**Status:** **FIX PLAN — NOT APPROVED FOR PRODUCTION**. Must review CURRENT HEAD again before edits.

## Objective and absolute boundaries

Finish the incomplete v2.1 Sales pricing flow safely. Fix new linked-document item/source-identity validation and price/discount/tax/override authorization, then supply independent SQL Server and browser evidence that the existing costing and quantity logic behaves identically for legitimate transactions.

> **DO NOT CHANGE:** Inventory/Cosing/FIFO/WAC/StockFifoLayer/CostLayer, `StockLedger`, `IvBalLoc`, `StockValuationFact`, `IvTrxBatch/History`, quantities, `StdQty`/`StdPsize`, frozen pack computation, `IvQty`, `IvItemPackPolicy`, SO/DO/Invoice reservation and allocation algorithms, `PostOneAsync`, `RollbackOneAsync`, customer-return `CostPrice`, Production/WIP, QA costing, stock-future-date/closed-month controls, and `SaInvoiceCalc` formula/rounding. No schema changes or backfill in the initial safe-fix gates. If implementation would require altering any protected code, **STOP** and request separate explicit approval.

**Do NOT redo correct G1–G5 work** or replace the pricing resolver. Keep current UOM, class/currency lookup and commercial DTO transport. Avoid unrelated UI refactors.

## Gate F0 — CURRENT baseline and mandatory proof (GPT-6 High independent investigator)

1. Verify current branch SHA; compare against reviewed `1b2d706...`, inspect worktree and intervening changes. Do not checkout an old replay branch or overwrite user's working tree.
2. Verify current implementation exactly, including all source-to-document copy paths, `SaPriceOverridePolicy`, DTOs and existing tests. Read existing gate files, but **do not treat the implementer's PASS as independent evidence**.
3. Prepare isolated SQL Server **test-only** tenant with seeded SO-A, SO-B, posted DO-A, direct and linked invoice, free-of-charge/manual invoice, stock-controlled BOX→PCS item, zero-stock/non-stock item, posted return CN and known cost layers. Prepare concurrent SQL sessions. No posting to customer production DB.
4. Record before state: sales document Qty/StdQty/StdPsize/selling and standard UOM; SO/DO/Invoice source-link consumption; reservations; warehouse/lot `IvBalLoc`; `StockFifoLayer`, `StockCostState`, `StockValuationFact`, `SalesReturnCostAllocation`; `IvTrxBatch`/history; post/rollback timestamps/status; month-end/future-date decisions; physical unit costs. Record commercial price/discount/tax separately.
5. Run exact current build and relevant unit/integration suites, including new pricing tests and latest backdated FIFO/WAC replay tests. Record passed/failed/skipped and environment. Check actual SQL Server concurrency behavior, not EF InMemory/SQLite alone.
6. Enumerate existing **saved or posted** mismatched source-item/price data with **read-only** queries; distinguish historical anomalies from new writes. Never automatically repair or recalculate historical posted lines.
7. Produce `F0_BASELINE.md` with SHA, changed file allowlist/denylist, fixtures, source-line field matrix, current acceptance and tests. If no SQL Server, mark **BLOCKED (UNVERIFIED)** rather than PASS.

## Gate F1 — CRITICAL source ITEM integrity without touching quantity formulas (DeepSeek V4.1 Flash High; GPT-6 High review)

**Files:** `ErpWeb.Core/Sales/SaDoService.cs`, `SaInvoiceService.cs` (ONLY validation statements in `PrepareLinesAsync`); `ErpWeb.UI/Sales/Transactions/SaDo.razor`, `SaInvoice.razor` (disable item substitution on linked line); relevant service/UI tests.

**Root defect:** Source-loaded `SaSoDetail`/`SaDoDetail` controls physical pack and fulfilment allocation but does not match submitted `line.ICode` to persisted source `ICode` on normal SO→DO, SO→Invoice or DO→Invoice flow. DR-specific comparison is not sufficient.

**Exact implementation:**
- Reuse already fetched `salesOrderDetail`, `linkedSoDetail`, `linkedDoDetail`; enforce the same tenant and branch, exact current revision, intended line and case-insensitive normalized *source item identity*. Must be done BEFORE prepared line is accepted. Reject with `Lines[i].ICode` and message “This item differs from the selected SO/DO line. Remove and reselect the source line.”
- For `LinkDo` invoice, the persisted **DO detail is authoritative**; if it itself has an SO reference, do not independently rewrite the SO linkage. If source SKU already differs historically, report and block **new** inconsistent child billing pending business decision; do not rewrite existing posted DO or issued stock.
- Lock the item picker (or disable replacing its code) when editing a source-linked DO/Invoice line. Permitted field/qty edits remain as before; changing to a different item requires removing and selecting a different source line, with existing reservations/shipment controls unchanged.
- Keep all quantity/pack/reserve computations **byte-for-byte unchanged**. No new transaction lock or SQL isolation behavior. Reuse existing validation error and rollback transaction behavior.
- Add positive and negative tests for A≠B, both stock-controlled and non-stock; direct DO/Invoice unchanged; mismatched source may not save or issue stock; valid linked line leaves every Qty/cost side effect unchanged; concurrent source revision still checked. Add persistence/reload and user-error tests.

**F1 PASS:** A new linked line cannot refer to SO/DO Item A while saving Item B; valid linked transactions post/rollback exactly as baseline; denylisted diff clean. **STOP for independent review.**

## Gate F2 — CRITICAL linked commercial identity & override declaration (GPT-6 High design; DeepSeek V4.1 Flash High implementation with GPT-6 independent review)

**Files:** commercial-only validation in `SaDoService.cs`, `SaInvoiceService.cs`, if necessary `SaSoService.cs`, `SaCdnService.cs`, `SaPriceOverridePolicy.cs` and corresponding UI/tests. **Do not modify unrelated physical methods.**

**Correct source fidelity:**
- For **new SO-linked DO**, DO price/discount/tax basis/tax group/order type must match **persisted SO detail**, absent a separately approved legitimate local change; DO normally is not a repricing screen.
- For **new DO-linked Invoice**, compare **persisted DO detail** to line `UnitPrice`, percentage 1–6, amount 1–2, `IsInclusive`, `TaxGrCode`↔`TaxGroup`, and `OrderType`; for direct SO-linked Invoice compare SO detail. Do not compare computed `TaxAmt` because invoice date/current law may differ.
- For saved **existing NEW** child documents, use own persisted line as local baseline, and preserve its legitimate prior commercial changes; do not compare to today's master or blindly reject historical differences.
- For a source line whose price was overridden upstream, child copying that **unchanged** agreed price does **not** require another `PRICE_OVERRIDE` permission. Source audit is reachable via `SoNo/SoLine` or `DoNo/DoLine`; don't impersonate an invoice-local override.
- A *new local* price difference from inherited source must be either (a) **blocked** if document policy forbids a new price, or (b) checked against server-side source price and authorized with `PRICE_OVERRIDE` plus meaningful reason. Business owner decides whether Invoice may override sourced price; existing UI should only expose permitted behavior.
- A changed discount/tax basis is **not automatically authorized** by PRICE_OVERRIDE. Reuse existing discount/tax validation and documented business rules; if approval scope unresolved, block that subcase rather than silently allowing.
- Never rely on client-controlled `OriginalUnitPrice`, `PricingSource` or `PricingRef` to prove rights; null or forged values must not bypass checks.
- **Direct sales/FOC backward compatibility:** DO NOT globally require direct `UnitPrice == today's resolver price`. Current tests permit manual and `UnitPrice=0`; keep those accepted until a separate explicit FOC/manual-price policy is approved. Never blanket-block existing posted or imported historical documents.
- Validate with same DB unit of work and existing expected RowVersion/revision checks; no new lock order, pricing-master round trips or altered transaction atomicity.

**F2 tests:** tampered linked Invoice with `OriginalUnitPrice=null`, forged original, source discount/amount2 altered, inclusive flag swapped, tax group altered, changed item, price changed by authorized and unauthorized operator, inherited upstream override, historical existing draft edit, legitimate direct FOC and manual invoice, transactional no-partial-save, posted document read unchanged. Also Sales e-Invoice tax/source regression. **STOP for independent review.**

## Gate F3 — Release compatibility, UI policy and corrected inquiries (DeepSeek V4.1 Flash High; GPT-6 High review)

- **Currency:** Read-only tenant-data audit of company `CurrencyCode` and special/list blank/explcit foreign prices. Preserve new safer base currency matching; do **not** introduce automatic FX conversion. For businesses relying on formerly permissive behavior, obtain signed operator policy and populate explicit price masters through normal authorized maintenance. Do not rewrite posted Sales lines.
- **Price visibility:** Verify `VIEW_PRICE` business policy. Keep actual document unit price visible to authorized Sales document users; show detailed current-price master ladder only when its projection permission allows. If service-level exposure needs a guard, implement a **read-only permission-aware projection**, never block underlying `ResolveLinePricingAsync` used for normal entry.
- **Price Inquiry:** switching items should choose the new item commercial UOM by default (unless a deliberate valid UOM selector is supported). No changes to stock UOM or pack.
- **UI:** In inherited popup show source doc and agreed price, disable item change, retain qty/tax UI in allowed states, avoid extra clicks, align with standard DevExpress Sales toolbar/dialogs, show clear human-readable validation messages.

**F3 tests:** users with Access but no VIEW_PRICE cannot see unauthorized master candidates; do not block normal Sales entry; switching BOX/PCS items selects right price UOM; USD/SGD direct matching behavior tested; linked historical FX price stays frozen. **STOP for independent review.**

## Gate F4 — **separate business decision only**, not auto-implementation

**B1 DiscountMethod snapshot**: Current customer method can change JOIN vs sequential net price (100 with 10%+5% => 85 vs 85.50). Requires a separately approved minimal **commercial per-line** snapshot across source docs and DTOs; don't alter `SaInvoiceCalc` formula, stock-cost logic or posted historical amounts. Business should decide whether urgent contract drift warrants schema approval.

**B2 CN amount-discount second slot**: Invoice `ItemDiscAmount1` is lost on copied credit notes. Existing warning is **not** equivalent to accurate credit. Propose a **commercial-only CN field** and end-to-end map as separate opt-in schema plan, or adopt explicit manual credit review. Never repurpose `CostPrice`, CR posting quantity or return allocation.

**B3 Direct/manual/FOC stricter price permission** and **B4 historic tax policy** also need separate sign-off. Do not quietly enforce or migrate under F1–F3.

## Gate F5 — Actual SQL Server physical equality, concurrent posting and browser UAT (DeepSeek V4.1 Flash High executes tests; GPT-6 High final independent review)

**Scenario matrix:**
1. QT→SO accepted price, source override, partial DO, partial Invoice; source amounts/discounts; no unexpected reprice.
2. SO Item A→DO Item A→Invoice Item A, stock-controlled BOX/PCS, master pack/UOM changed after SO. Compare exact StdQty/StdPsize, SO reserved/consumed, stock issue, cost layer and rollback.
3. SO Item A→DO/Invoice Item B **must fail before any stock or source-consumption side effect**; no partial document/rows.
4. Several posted DOs→one Invoice, different price/discount/tax; lines preserve each source and post/rollback with existing limits.
5. Direct Invoice (stock & nonstock), legitimate 0/manual price; posted/rollback; unchanged inventory cost and future-stock gates.
6. CN copied from Invoice, `ReturnStock=true`/false, lot/warehouse and original `CostPrice`, post/rollback; same original invoice cost allocation.
7. Future date/closed period, stale RowVersion, duplicate/concurrent save/post/rollback, partial shipment, backdated FIFO/WAC replay; no negative stock/cost imbalance.
8. DR→WO/Production and QA/WIP downstream links; no new demand/fulfilment or stock effect from Sales price changes.
9. Real browser DO picker with posted DO; edit linked DO and Invoice qty; Price Info; CN second-discount warning; restricted-price user and foreign-currency direct sale.
10. e-Invoice generation and financial totals follow correct commercial terms without financial historical mutation.

**Required commands (adjust filters to actual repository traits):**
```
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "TestCategory=Sales"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaInvoiceServiceTests|FullyQualifiedName~SaDoServiceTests|FullyQualifiedName~SaCdnServiceTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~SaItemFamilyPricingContractTests|FullyQualifiedName~SaLinePricingConsumerTests"
```
Then execute Inventory, StockLedger, Costing, Production, QA and e-Invoice tests by **actual project trait names** (inspect traits first, do not invent names). Execute SQL Server physical/concurrency tests on an **isolated disposable DB** with the harness's required connection variables; count and report skipped tests. Do not claim actual SQL pass from SQLite or a report written by a different agent.

**Comparison acceptance:** For unchanged legitimate physical inputs, `Qty`, `StdQty`, `StdPsize`, reserved/consumed quantities, warehouse/lot `IvBalLoc`, `StockFifoLayer`, `StockCostState`, `StockValuationFact`, Sales return allocation, `IvTrxBatch`/history, posting/rollback outcome, closed period and future-date behavior **must match baseline** (account for generated IDs only where business equivalence is proven). Corrected selling-price, net and tax differences are allowed **only** in identified commercial-defect cases; never use them to excuse physical deviations.

## Exit criteria and required AI agent report

- [ ] Current reviewed SHA pinned; protected diff denylist PASS, all modified service hunks inspected.
- [ ] Linked source item mismatch impossible on *new* DO/Invoice save; valid documents unaltered.
- [ ] Server independently validates new linked commercial terms and permission; `OriginalUnitPrice=null` tampering does not bypass linked checks.
- [ ] Direct zero-price/manual/imported document compatibility preserved.
- [ ] G1–G5 existing correct implementation retained, no duplicate resolver or architecture rewrite.
- [ ] Company FX rollout and price visibility policy approved.
- [ ] B1/B2 intentionally deferred **OR** separately approved and implemented in their own gated changes, with known-risk release notes.
- [ ] Stock/cost/quantity/rollback/period SQL Server baselines and full browser UAT proven at current SHA.
- [ ] No changes to core costing, quantity, `SaInvoiceCalc`, post or rollback logic; otherwise STOP.
- [ ] For every gate, report **PASS / FAIL / BLOCKED**, exact file and line diff, tests actually run, SQL fixture, before/after physical and money result, skipped cases and independent reviewer verdict.

**Approval condition:** NO "10/10" or production release until all critical safeguards and physical regression evidence are verified. If a fix would alter any physical calculation, escalate as a distinct, approval-gated inventory/quantity change; do not sneak it into pricing.

## Copy/paste AI Coding Agent prompt

> Connect to CURRENT `mokth/net10projectTemplate` `productionv2`. Use `productionv2_sales_pricing_IMPLEMENTATION_DEEP_REVIEW_20261111.md` and this Safe Fix Plan. Perform **F0 only, without code edits**. Verify current SHA, prove the source-linked item identity / price-override gaps and create test fixtures and physical SQL Server baseline. Do not alter FIFO/WAC/StockFifoLayer/CostLayer, IvBalLoc, Qty/StdQty/StdPsize, frozen pack, allocations, stock/production/WIP, posting, rollback or month-end rules. Report PASS/FAIL/BLOCKED; proceed to F1 only after independent GPT-6 High approval. Use DeepSeek V4.1 Flash High for focused F1/F3 implementation and GPT-6 High for F2 design and independent review. Do not implement commercial schema Phase B without explicit approval.
