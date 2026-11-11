# productionv2 — Sales Pricing Flow Correction
## Deep-review edition v2.1 — AI model and gate assignments • 10 November 2026

**Source of truth:** `mokth/net10projectTemplate`, branch `productionv2`, inspected commit `584411e317ac6990ed8a5f0886207779d821deaf`.

**Status:** **v2.1 MODEL-ASSIGNED — GATE 0 APPROVED TO EXECUTE; CODE CHANGES CONDITIONALLY BLOCKED UNTIL GATE 0 PROVES COMPATIBILITY.** This is not “10/10 approved for implementation.” The repository was read through the GitHub connector; **the application and tests were not run** in this review.

**Mission:** Fix incorrect Sales unit-price selection and accidental changes to agreed prices, discounts and tax-basis metadata across direct Sales documents and QT → SO → DO → Invoice → CN, **without modifying or destabilizing cost valuation, stock/quantity/WIP, or posting/rollback**.

> **HARD BOUNDARY:** Neither this plan nor its code agent may modify FIFO, WAC, CostLayer, inventory valuation, IvBalLoc, warehouse/lot quantity, Qty/StdQty/StdPsize computation, selling-to-standard UOM factors, SO/DO/Invoice allocation and consumption, receipt/issue/return postings, posting/rollback/reversal, period closing, stock future-date controls, or Production/WIP. Commercial values may be corrected only within Sales line preparation, commercial DTO transport, price resolution/validation, and UI price triggers. Any proposed fix that indirectly changes stock/cost results is a **stop condition**. A source-date/line validation that *blocks* a previously valid transaction is also a workflow regression: it must be evaluated, not dismissed as 'no quantity formula changed'.

---

# 0. AI model assignment and implementation protocol (v2.1 addition)

**Preferred primary coding model:** **Grok 4.7 High** in the coding agent/IDE, provided this exact model is available in the user's configured tool.  
**Independent reviewer and high-risk design model:** **GPT-6 High**.  
**Economy/limited-assignment model:** **Composer 2.5**, if available, for small and isolated UI or mechanical test changes only.  
**Fallback rule:** If a named model is not present or cannot access the current repository and run the required checks, the operator must select an available model of comparable capability and retain the same reviewer and safety gates. A substitution never waives testing, audit, or approval. Do not assume model availability or automatically skip a gate.

## 0.1 Exact per-gate assignments

| Gate | Implementer / reviewer | Scope and model boundaries | Required independent checkpoint |
|---|---|---|---|
| **G0 — Baseline, no code edits** | **GPT-6 High** (lead investigator) | Read the CURRENT `productionv2` checkout; map QT→SO→DO→Invoice→CN, price/discount/tax, source identity, quantity, costing and rollback invariants. Create evidence/test baseline. **Do not implement fixes.** | GPT-6 High signs `PASS / FAIL / BLOCKED` for G0; current code SHA and test fixtures recorded. |
| **G1 — DO→Invoice commercial DTO** | **Grok 4.7 High** (implementer) + **GPT-6 High** (reviewer) | Small DTO and commercial projection/VM mapping only; preserve quantity/pack/lot/source-consumption fields. | GPT-6 High reviews exact source/destination mapping and before/after physical snapshots before G2. |
| **G2 — Prevent inherited repricing** | **Grok 4.7 High** (implementer) + **GPT-6 High** (reviewer) | Pricing triggers and source-vs-direct classification only. Do not touch SO/DO/Invoice source quantity allocation. | GPT-6 High verifies no silent repricing and stable quantity/stock/cost for partial transactions. |
| **G3 — Discount source/class/UOM** | **Grok 4.7 High** (implementer) + **GPT-6 High** (reviewer) | Fix failed discount lookups, directional item class and commercial UOM. Preserve `SaInvoiceCalc` and frozen physical UOM. | GPT-6 High traces all resolver consumers, confirms amount/stock separation and repeat prior gate regressions. |
| **G4 — Currency & override** | **GPT-6 High** (lead implementer/reviewer separated by a second review pass) | Highest policy/security/legacy-compatibility risk. Implement **only the approved A5/A6 substeps** after evidence and business sign-off. A separate independent review pass must inspect the completed diff; do **not** treat self-review as independent sign-off. | Require explicit policy approval for foreign currency, direct/manual/FOC and strict server authorization changes; block undecided items. |
| **G5 — UI & full regression** | **Grok 4.7 High** (lead implementer); **Composer 2.5** (optional bounded tasks); **GPT-6 High** (reviewer) | Reuse existing DevExpress Sales UI, price explanations, non-mutating UAT/tests. Composer is limited to presentation and mechanical tests, working from a narrowly specified task; it must **not** independently alter financial rules or transaction services. | GPT-6 High reviews diff, business UAT, stock/cost snapshot equivalence and unmodified protected logic. |
| **Final independent review** | **GPT-6 High** | Review CURRENT implemented code, all gate artifacts and tests, and protected file/method diff. Do not trust prior plan score as proof. | Only mark approved if all required evidence passes; otherwise issue a fix list and re-run affected gates. |
| **B1–B4 (optional)** | **GPT-6 High** design/review; implementation model chosen separately | **NOT AUTHORIZED** by this plan. Discount-method snapshot, CN second amount slot, stricter legacy override and tax policy need separate approval and regression scope. | No implementation, migration or change to protected logic unless a new approved plan explicitly authorizes it. |

## 0.2 Coding-agent work contract

1. **Do not run all gates in one long autonomous job.** Start with **G0 only**; once it passes, implement **one code gate at a time**, stop, run specified tests and hand the evidence to GPT-6 High for review. The reviewer can return `PASS`, `FAIL` or `BLOCKED`; only `PASS` advances the next gate. G4 policy may be deferred; record the deferral and its user impact rather than pretending it was fixed.
2. **CURRENT branch is the truth:** refresh `productionv2` SHA and repository state before each gate; inspect any intervening costing work and avoid overwriting uncommitted changes. A separate feature branch is preferred. Do not copy assumptions from the previous checkpoint if code moved.
3. **Commercial-only editable area:** implementation models may change Sales price/discount/currency projection, validation, user-facing price triggers and corresponding tests **only** as permitted in §3. The denylist, stop conditions and protected equality tests in §§3, 8 and 9 apply equally to Grok, GPT-6 and Composer.
4. **Independent review artifact for every gate:** current SHA, task/model, exact changed files and hunks, before/after Sales price+tax cases, physical baseline comparisons (`Qty`, `StdQty`, `StdPsize`, reservations, posted stock, FIFO/WAC/CostLayer, rollback), test commands and actual results, unverified cases, and a `PASS / FAIL / BLOCKED` decision. Code review is incomplete if it merely says "no costing files edited."
5. **Escalate unexpected scope:** if G1–G5 need a `CostLayer`, posting, allocation, transaction atomicity, `IvItemPackPolicy`, `IvQty` or WIP change, stop that subtask. **No model has standing authority** to approve it. Also stop if a new validation unexpectedly blocks a previously valid Sales transaction, until the business impact is assessed.
6. **Be budget-conscious without weakening controls:** reserve GPT-6 High for G0, G4, high-risk PR reviews and final sign-off; use Grok 4.7 High for the focused code changes; use Composer 2.5 only for permitted UI/low-risk test work. If the reviewer finds a defect, the same implementer can fix it but the reviewer must recheck.
7. **Do not inflate an approval score:** G0 approval means only "approved to investigate"; G1–G5 passes certify specific completed gates; **the entire plan or implementation is not automatically 10/10 approved**. Phase B remains separate.

## 0.3 Precise IDE/coding-agent prompts

**Prompt to GPT-6 High for G0 (START HERE):**

> Inspect the CURRENT `mokth/net10projectTemplate` branch `productionv2` and execute **Gate G0 ONLY** from the attached Sales Pricing Flow Plan v2.1. No code edits. Trace QT→SO→DO→Invoice→CN plus direct Invoice/DO, prove baseline quantity/pack/stock/cost/rollback invariants, document commercial source/discount drift, collect test and SQL fixture evidence. Return `PASS / FAIL / BLOCKED`, gaps, and a G1-ready exact change map. Do not modify any costing, quantity, posting, rollback, production/WIP or closed-period logic.

**Prompt to Grok 4.7 High for G1–G3 (use one gate number per run):**

> Start from the CURRENT `productionv2` HEAD and the passed prior gate. Implement **G[1/2/3] ONLY** as defined in Plan v2.1. Work in allowed Sales commercial code and tests, with minimal diffs. Preserve every protected costing/quantity/stock/posting/rollback invariant. Execute the gate tests, compare physical snapshots to baseline, list exact changed file hunks, and STOP for independent GPT-6 High review. Do not move to another gate or implement B1–B4.

**Prompt to GPT-6 High for G4 (only after explicit policy decision):**

> Implement only the business-approved G4 currency/override substeps in Plan v2.1. Preserve free-of-charge and historic/manual-price compatibility; do not introduce FX price conversion, unapproved schema changes or cost/quantity/posting modifications. Run tampered-request, permission, tenant, linked-source, direct-price and protected physical tests. Submit code and evidence for an additional separate final review pass; stop any ambiguous policy item.

**Prompt to Grok 4.7 High for G5; optionally delegate bounded UI tests to Composer 2.5:**

> Complete G5 Sales pricing UI/explanation and regression work only, using the repository's existing DevExpress transaction patterns. Composer 2.5 may handle isolated UI label/alignment or mechanical test code if present, not pricing services or financial logic. Run the whole supported commercial and physical regression matrix. Provide evidence for independent GPT-6 High final review. Do not change stock/cost or posting behavior.

---

# 1. Review verdict: what v1 got right, and what v1 missed

| # | Finding from current source | Risk to original plan | Required v2 correction |
|---|---|---|---|
| R01 | `SaDoBillableLineDto` lacks `ItemDiscount...6`, `ItemDiscAmount/1`, `IsInclusive`, `TaxGroup`, `OrderType`, and source standard-UOM display metadata. `SaInvoiceLineVm.FromDeliveryOrder` copies only price/provenance. | **Critical**: DO→Invoice may lose discounts, basis and commercial description. | Add a strictly **commercial-only** DTO/mapping projection from saved `SaDoDetail`; do not copy packing/qty factors into calculations. |
| R02 | `SaDo.razor.cs` quantity handler (~1212), `SaInvoice.razor.cs` quantity handler (~1665) can call live resolver on source-linked lines. | **Critical**: master changes can reprice an already-agreed partially delivered/invoiced line. | Classify each line as direct-new, source-inherited or saved-existing; suppress automatic master pricing only for inherited/saved prices. |
| R03 | `SaSalesRefService.ItemFamily.PriceSources.cs:75–78` silently uses **no discount** whenever `ResolveItemDiscountAsync` fails, not only when no rule matches. | **High, NEW**: transient DB/tenant/validation failure can silently overcharge. | Treat genuine `None` selection as success, but return a pricing error when discount lookup *fails*. No zero-discount fallback on failure. |
| R04 | `SaDisGroupItem.IClass` matches item class, but pricing consumers pass `IvStockMaster.Classification`, or omit it. `SaItemFamilyRuleMatch.Applies` uses symmetrical `ClassesOverlap`, which makes a blank request match a class-specific rule. | **High**: incorrect class-specific discounts. | Read authoritative `IClassCode` on server; use **directional runtime match**, retain symmetrical conflict-overlap check for rule maintenance. |
| R05 | QT/SO send selling UOM; direct DO/Invoice/CDN send standard UOM, while physical factor uses the selling/standard pair. | **High**: wrong commercial price lookup, with potential downstream totals mismatch. | Only standardize the UOM passed to the **price resolver**. Do not touch StdQty, pack factors, frozen source snapshots or stock transaction fields. |
| R06 | Blank candidate currency is accepted against any document currency by `CurrencyMatches`, although master says blank = company base. | **High**: base-currency numeric amount may be treated as foreign-currency amount. | Define verified effective document/base currency and fail closed for **new direct pricing**. Separate foreign-currency compatibility rollout; never reprice legacy source documents. |
| R07 | `SaPriceOverridePolicy.IsOverride` depends on client-supplied `OriginalUnitPrice`, which can be omitted. However current services/tests allow direct/manual and even **zero** invoice prices despite a nonzero master price. | **High**: blanket “re-resolve at every Save” will break existing business/API tests and historic records. | Stage authoritative checks with **separate direct-new, saved-existing and linked-source contracts**; no blanket new restriction until compatibility inventory and explicit business sign-off. |
| R08 | `SaInvoiceCalc.CalculateLine` uses **current `SaCust.DiscountMethod`** (`JOIN`/sequential), and QT/SO/DO/Invoice details do **not** store a discount-method snapshot. | **Critical, NEW**: frozen discount percentages do not guarantee frozen net price if customer discount method changes between SO and Invoice. | Record the limitation, test it, choose an approved **commercial-only per-line snapshot** design separately if strict net-price immutability is required. Do not rewrite discount arithmetic to disguise the issue. |
| R09 | `SaCdnService.CopyFromInvoiceAsync` copies only `ItemDiscAmount`; `SaCdnLineRequest`/`SaCdnDetail` do not provide a second amount-discount slot equivalent to Invoice's `ItemDiscAmount1`. | **High, NEW**: v1 promised fully identical Invoice→CN discounts, not representable by current CN schema. | Preserve existing CN stock-return logic. Scope a **separate approval** for a commercial-only CN discount-slot schema change or record this known limitation. Never silently claim complete CN fidelity. |
| R10 | `SaInvoiceService.cs` direct SO linked path (~2295–2306) compares current master `SellingUom` with the saved SO selling UOM, even while quantity preparation already uses source frozen pack. | **High, NEW**: changed Item Master UOM can block legal fulfillment of an existing SO. | Check only commercial-UOM **validation** against source snapshot (under existing source/pack gates); never recalculate quantity or relax source identity. |
| R11 | `SaInvoiceCalc` uses current tax group/rate and existing adaptive rounding at Invoice; `TaxGrCode` and `IsInclusive` are copyable, **historical numeric tax rate is not frozen** by the existing line DTO. | **High**: “copy tax” must not mean copy old computed tax amount or force rate. | Copy source **tax group and inclusive flag**, then let existing Invoice tax calculation determine amount. Tax-rate policy changes are a separate accounting decision. |
| R12 | `SaCdnService.CopyFromInvoiceAsync` copies invoice `OriginalUnitPrice` + `OverrideReason`, potentially treating an upstream override as a new CN override. The CN credit-return batch uses `CostPrice`, not selling price. | **High**: wrong permissions/provenance can block returns; cost edit would be dangerous. | Separate **upstream provenance** from **new CN-local price decision**. Do **not** modify `CostPrice`, `SourceInvLine`, return quantity, stock journal/batch. |
| R13 | Source line price provenance (`PricingSource`, `PricingRef`) is explanatory and is accepted from save requests; not independently authenticated as a price decision. | **Medium/High**: “price source” can be misleading if request tampered. | Do not treat provenance tokens as authorization or price truth. Verify commercial source/baseline only where it can be done safely without breaking historical/manual paths. |
| R14 | The prior test matrix said “customer special with noninteger legacy MOQ,” but `SaItemCust.MOQ` is an integer in the live contract. | **Low**: invalid test premise. | Test integer MOQ boundaries and fractional sales quantities, **not** a nonexistent fractional stored MOQ. |

**Conclusion:** v1 had the correct general objective but was **not safe to call implementation-ready**. In particular it overlooked `DiscountMethod` drift, CN's missing second amount slot, silent discount-fetch failures, and incompatibility between strict new override gates and legacy/FOC behavior. v2 splits confirmed minimal corrections from business-policy/schema decisions.

---

# 2. Current architecture to KEEP

**Do not invent a second pricing engine.** The current flow is:

1. `Company.SalesPriceMethod` → `SaCompanyPriceMethod.ResolveSources()` → fixed eligible-source walk.
2. `SaSalesRefService.LoadPriceContextAsync()` → `SaItemFamilyPriceResolver` → customer special, customer list, group list, item default, date/quantity/UOM/currency eligibility.
3. `SaItemFamilyPriceBasis` → stored line price basis (inclusive or exclusive).
4. `SaItemFamilyDiscountResolver` + `SaInvoiceCalc.CalculateDiscountPerUnit` → percentage/amount slots and customer `DiscountMethod`.
5. `SaInvoiceCalc.CalculateLine`/`ApplyTaxAdaptiveRounding`/`CalculateHeader` → money and tax totals.
6. UI line DTO → Sales service `Prepare...` → **existing** DB transaction → existing posting/rollback methods if posted.

**Already good and preserved:** price-list effective periods, quantity tiers, customer/group priority, price source/ref explanation, `PRICE_OVERRIDE` permission + reason concept, bulk price maintenance history, `SaPriceInfoDialog`, Price Inquiry, accepted Quotation conversion to SO, source frozen stock UOM pack snapshots on DO/Invoice, separate sale price from CN return cost.

**Important behavior distinction:** price source, discount slots, **discount calculation method**, tax basis, tax-group code and transaction currency are *commercial context*. `StdQty`, pack factor, stock-controlled flag, lot/warehouse, shipments, reservations, `SoConsumedQty`, `QtConsumedQty`, `CostPrice`, FIFO/WAC are *physical/cost context*. Do not conflate them.

---

# 3. Scope and protected files

## 3.1 Allowed **only after gates pass**

| Change type | Source files/components | Exact allowed work |
|---|---|---|
| Shared price/discount rules | `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs`, `.Resolve.cs`, `SaItemFamilyPricing.cs`, `SaLinePricingResults.cs` | Discount-fetch fail handling; authoritative class; currency context; commercial price request only. No new arithmetic or new pricing priority. |
| Commercial source DTO | `ErpWeb.Core/Sales/ISaDoService.cs`, `SaDoService.SearchBillableLinesCoreAsync` | Add/read **commercial** fields from stored DO detail, with tests. |
| Invoice commercial mapping | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs` | Copy source commercial terms into line VM; distinguish source-new from direct-new. **Do not change any stock/quantity mapping or invocation.** |
| Other Sales UI entry | `SaQt.razor[.cs]`, `SaSo.razor[.cs]`, `SaDo.razor[.cs]`, `SaCdn.razor[.cs]`, `SaInvoice.razor` | Reprice triggers, source status labels, correct commercial UOM/context, user messages, controlled override UI. |
| Source commercial verification | **selected commercial sections only** in `SaSoService`, `SaQtService`, `SaDoService`, `SaInvoiceService`, `SaCdnService` | Authoritative source-price and discount/tax-basis checks. Must be additive and compatible, preferably reuse already loaded source detail. |
| Pricing UI/access | `SaPriceInfoDialog` and `PriceInquiry` | Match explanation context and existing `VIEW_PRICE`/Sales access policy without blocking required Sales entry. |
| Tests | `ErpWeb.Tests/Sales/**` + read-only comparison tests across Sales→Inventory | Add targeted tests and previous behavior regression coverage. |

## 3.2 Explicit denylist

**Never edit:** `ErpWeb.Core/Costing/**`; `ErpWeb.Core/Inventory/IvInventoryPostingService*`, `IvStockCountService*`, `IvItemPackPolicy`, `IvQty`; `ErpWeb.Core/StockLedger/**`; `ErpWeb.Core/Production/**`; warehouse/lot/stock service; posting batch/stock history logic; `IvBalLoc`, `CostLayer`, `IvTrxBatch`, `IvTrxBatchDetail`; Production WIP; allocation/reservation/consumption services; `SaProductionDemandRules.ResolveFrozenStdFactor`; `SaSoQty`; `SaInvoiceCalc` formulas/rounding; **Sales `PostOneAsync`, `RollbackOneAsync`**, source issue/receipt/CR batch builders; month-end/future-date controls; accounting/e-Invoice financial conversion algorithms.

**No schema changes in automatic Phase A.** Sales **commercial-only** schema extension, if found necessary for `DiscountMethod`/CN amount2 snapshot fidelity, is an **approval-gated Phase B proposal**. It is not authorized by this plan. No stock/cost schema changes, under any circumstances. Do not update existing posted rows or perform pricing backfills in either phase.

**Protected source file rule:** `SaDoService.cs`, `SaInvoiceService.cs` and `SaCdnService.cs` contain BOTH commercial and stock/posting logic. A filename allowlist is not enough. The agent must submit a **hunk-by-hunk diff** proving it did not edit physical/cost/rollback logic. If a change touches protected lines, stop.

---

# 4. Detailed behavioral contract (prevents contradictory fixes)

| Context | Automatic master repricing | Qty / stock behavior | Editing/commercial policy |
|---|---|---|---|
| Direct **new** QT/SO/DO/Invoice line, no accepted price yet | **Yes**, based on item/customer/selling UOM/qty/date/currency. | Current request Qty and existing conversion untouched. | Price override by existing privilege/reason; no-price handled explicitly, including existing FOC tests. |
| Direct newly added line whose operator entered an authorized manual price | **No silent overwrite** on blur or quantity/date change. | Keep Qty and calc unchanged. | Warn that pricing inputs changed; require explicit reprice or retain authorized override. |
| Saved draft line reopened, unrelated field changed | **No**. | Existing saved Qty logic unchanged. | Preserves saved commercial value, even if today's master differs. |
| Accepted QT → SO (conversion) | **No**. | Preserve quote-source consumption logic. | Copy agreed commercial snapshot, including source provenance. |
| SO-linked DO, partial quantity | **No**. | **Exactly** existing partial shipment, reserve, StdQty and pack snapshot behavior. | Unit price and percentage/amount discount slots inherited; recalculate extended money using existing line engine. |
| SO-linked Invoice, partial quantity | **No**. | **Exactly** existing SO billed/reserved consumption. | Inherit SO price and commercial fields. |
| DO-linked Invoice, partial/multiple DOs | **No**. | **Exactly** existing DO billable quantities and per-line stock snapshot. | Inherit commercial fields from EACH source DO line; do not merge or average prices. |
| Direct new Invoice from scratch | **Yes**, only before explicit manual price accepted. | Current direct-stock issue logic unchanged. | Resolve source using commercial selling UOM, not standard UOM. |
| CN copied from posted Invoice | **No**. | **Exactly** existing original invoice line/CostPrice/stock return constraints. | Inherit supported price/tax/discount fields; second amount slot is a documented schema limitation until separately approved. |
| Direct CN/DN | Existing standalone commercial resolution | Existing CN return-stock policy untouched. | Must distinguish stock return from non-stock adjustment; no automatic linking to a fabricated invoice. |
| Posted, closed, externally submitted or historical document | **Never**. | Posting/rollback/status/period gates unchanged. | Read and display stored values, no backfill/repricing. |

**Price versus tax:** Freeze source **unit price and discount slot inputs**, plus `IsInclusive` and `TaxGrCode` where supported. Do **not** copy calculated `Amount`, `NetAmount` or `TaxAmt` as new invoice totals; those depend on destination quantity and current legal/tax policy and must be calculated via existing `SaInvoiceCalc`. The tax-rate-on-document-date question is *not* solved here. Preserve current tax lookup and existing validation for one invoice's tax basis.

**Discount method:** In current code `SaInvoiceCalc.CalculateLine` uses current `customer.DiscountMethod`. Therefore this plan **cannot promise unchanged net prices when a customer switches JOIN ↔ sequential between source and invoice without a new commercial snapshot design**. The bounded no-schema fix will preserve the discount slots and expose a mismatch risk in test/evidence. See Phase B.

**UOM:** Commercial lookup UOM ≠ `StdUom`. A linked line must rely on the saved source's commercial UOM identity; no conversion of a price by pack factor, no changing `StdPsize`, `StdQty`, `SellingUom` snapshots, or any quantity/lot field.

**Legacy free-of-charge:** `SaInvoiceServiceTests.Submitted_zero_price_and_pack_gl_from_master` proves `PACK1` with master `SellingPrice=8`, `SellingUom=BOX`, `StdPackSize=12` can currently save a direct invoice at **UnitPrice=0**, then update after the master pack changes while keeping zero price. A blanket mandatory engine-price check at Save would **break a current supported test and change business workflow**. No global “price must be >0” policy in Phase A.

---

# 5. Gate 0 — repository audit and safety baseline (**execute FIRST, no code changes**)

1. Confirm live `productionv2` HEAD, local worktree/branch status, uncommitted changes, migrations and feature flags; if HEAD changed, revalidate all line references and adjust plan. No edits to existing user work.
2. Map **all ways to create and edit** QT, SO, DO, Invoice, CN/DN: UI New/Edit, QT conversion, SO picker, DO picker, direct save services/API, return copying, bulk/import, revision and rollback/repost. Also trace **all quantity-change handlers and price-regeneration triggers**.
3. Create a **commercial field matrix** per route: `UnitPrice`, 6 percentage slots, amount1/amount2, `IsInclusive`, `TaxGrCode`/`TaxGroup`, `OrderType`, selling UOM, discount method, pricing source/ref, original price/reason, currency/rate. Mark missing DTO/schema fields explicitly.
4. Create a **protected physical matrix**: Qty/StdQty/StdPsize, original source line/revision, SO/DO billed/shipped/balance, stock-control flag, warehouse/lot, CR `SourceInvLine`, CR `CostPrice`, `IvBalLoc`, `CostLayer`, inventory batch/history, WIP downstream. Save before-state snapshots of selected normal, partial, future-date, closed-period and rollback cases.
5. Verify `SaInvoiceService.cs` source SO UOM check (~2295–2306), frozen source pack (~2332 onward), DO frozen pack (~2526 onward in DO service), CN return cost (~2385 in CDN service), and current company discount-method usage in all five Sales services.
6. Specifically document current direct zero-price/manual-save behavior and relevant permissions (quotation and invoice existing tests accept submitted prices, including zero). Catalogue whether any integration uses intentionally negotiated prices *without a registered master price*. Do not break it.
7. Establish **customer discount method drift** test (`JOIN` at SO, `SPLIT` at Invoice), a source with `ItemDiscAmount1 != 0` copied to CN, and a master `SellingUom` changed after an SO was issued. Record actual behavior (run in isolated test DB).
8. Capture baseline tests and outputs: `dotnet build ErpWeb.slnx`, Sales unit/service tests, Inventory posting/rollback/CostLayer/stock balance tests, e-Invoice and affected accounting reporting tests as available. **If runner/SQL Server unavailable, mark UNVERIFIED rather than PASS.**
9. Confirm tax policy with project business owner before any claim of historical tax-rate immutability. A downstream invoice can be subject to current tax rate; source tax group/basis cannot substitute for a legal effective-dated tax requirement.
10. Confirm UI rights: `VIEW_PRICE` for price-list/projections versus Sales entry right to see an actual document unit price; do not use one permission to deny the other accidentally.
11. Define **no-schema Phase A** scope and **conditional Phase B** asks. Record exact current baseline values and file/line-hunk denylist.

**Gate 0 deliverables:** (a) data-flow diagram; (b) per-route field matrix; (c) 15+ baseline cases and actual test output; (d) compatibility register for manual/zero prices, discount-method drift and tax; (e) snapshot comparison SQL/assertions; (f) gate decision `PASS / FAIL / BLOCKED` signed by reviewer.

**Gate 0 PASS** only when existing behavior is proven and Phase A edits are demonstrably confined to commercial inputs/projections. If any requested fix requires a new discount-method/CN slot schema, place **only that subtask** in Phase B, not in Phase A.

---

# 6. Phase A — no-schema, no-cost/quantity code changes (one small gate at a time)

## A1. Repair DO → Invoice commercial transport (highest certainty, lowest physical risk)

**Files:** `ISaDoService.SaDoBillableLineDto`, `SaDoService.SearchBillableLinesCoreAsync`, `SaInvoiceLineVm.FromDeliveryOrder`, associated tests. Read `SaDoDetail` as source of truth.

- Add and populate: `ItemDiscount`, `ItemDiscount2`–`ItemDiscount6`, `ItemDiscAmount`, `ItemDiscAmount1`, `IsInclusive`, `TaxGrCode` (from `SaDoDetail.TaxGroup`), and `OrderType` where available. These fields are **commercial only**. Optionally expose source `StdUom` for **display/price inquiry only**; do not use it to compute/replace invoice StdQty or frozen pack.
- Copy into invoice VM **verbatim from each source DO detail**, not from today's customer price or discount master. Keep `UnitPrice`, `PricingSource`, `PricingRef` copied.
- **Do not copy `OriginalUnitPrice`/`OverrideReason` as a new invoice-local override declaration**. The source's override already happened on the source; preserve provenance via source `DoNo/DoLine`. A new invoice-local change must have its own reason/permission.
- Ensure the source DTO maps its tax-group code to the Invoice VM's correct property. Current source `SaDoDetail.TaxGroup` differs from destination `TaxGrCode` spelling.
- Do **not** copy old source `Amount`, `NetAmount`, `TaxAmt`, `StdQty`, `StdPsize`, `SoConsumedQty`, source revision or shipments into new economic calculations. Existing service preparation owns quantity/stock conversion and amount computation.
- In mixed-DO (one invoice, multiple DOs), never use the first DO's price/discount for later lines. Preserve the current “cannot mix direct SO with DO lines” check.

**A1 pass:** same source commercial terms on partial invoice line; no source pack/stock/quantity/CR side effects; tests assert all 6 percentage slots, both amount slots, basis, tax group and `OrderType`.

## A2. Prevent automatic repricing of source-linked lines

**Files:** Sales transaction UI `.razor.cs` handlers and one **small shared helper** only if it reduces duplicated logic.

- Classify line **per instance** using real source identity, not `PricingSource`: QT→SO (`QtNo/QtLine` etc.), SO→DO, SO→Invoice, DO→Invoice, Invoice→CN; also saved drafts already carrying committed values.
- In DO/Invoice `OnPopupQuantityChangedAsync`: if source-inherited, change quantity as before **but do not call `ResolvePopupPriceAsync`**. Let existing `RecalcDocument` recalculate monetary extended totals. **Do not touch the quantity calculation or source allocation path.**
- New direct lines still request live price as quantity-tier input changes **unless operator already entered/confirmed manual price**; warn rather than overwrite manually accepted price.
- For item/tax/inclusive/date changes on existing/source lines: keep existing permissions and calculated amounts; do not automatically force live price. If source terms changed intentionally, require explicit source-compatible review rather than silent price reset.
- Keep page-specific UX: DO is primarily a shipping document; no unnecessary editing of price on SO-linked DO. Inherited prices display “from SO …” / “from DO …”.
- Cancel stale async pricing responses (`_priceApplySeq` pattern already exists) so an earlier price request cannot overwrite a newly imported source line. Include user-switch/currency/date cross-request sequencing tests.

**A2 pass:** SO RM10/100 pcs → master RM12 → partial DO 40 and Invoice 25 stay RM10, while extended amount recalculates normally; stock physical values exactly match baseline.

## A3. Discount-class and failed-lookup fixes

**Files:** `SaSalesRefService.ItemFamily.PriceSources.cs`, `.Resolve.cs`, `SaItemFamilyPricing.cs` + tests.

- Resolve the item's actual `IvStockMaster.IClassCode` in the tenant and use it for discount rule selection; **do not use `Classification`** as a substitute. Query only what is needed; no Item Master update.
- Create **directional runtime** class test `string.IsNullOrWhiteSpace(rule.IClass) || (actualClass nonblank && rule.IClass == actualClass)`. Keep the existing **symmetric** `ClassesOverlap` for overlap/conflict detection when saving discount masters. Prove these differ in tests.
- `ResolveLinePricingAsync` presently treats an unsuccessful `ResolveItemDiscountAsync` as `None`. Change to **return a clear pricing error** for true lookup/service failure; a successful `None` is still a valid **no applicable discount** outcome. Never turn DB failure into “0% discount.” Avoid logging sensitive pricing data.
- **No changes** to `SaInvoiceCalc.CalculateDiscountPerUnit`, `JOIN`/sequential arithmetic, discount order, tier formulas or rounding.

**A3 pass:** wrong class cannot receive a class-specific discount; blank rule works; failed discount query blocks rather than silently removing discount; no physical difference.

## A4. Direct commercial UOM consistency, with frozen-stock protection

**Files:** UI request-building only, possibly shared pricing-request helper.

- QT/SO already use `SellingUom ?? StdUom`. Align **new direct** DO/Invoice/CDN price request UOM to that **commercial selling UOM**. Do not repurpose `StdUom` fields in persisted entities or calculations.
- A direct transaction may use `BOX` commercially while its stock is deducted in `PCS`. **The existing stock factor decides this.** The price lookup must not perform additional conversion.
- A linked SO→Invoice uses the saved SO selling UOM even if today's master changed. Review `SaInvoiceService` source-UOM comparison that currently checks today's item master; only adjust the **commercial eligibility** comparison after proving frozen pack and source identity checks remain unchanged.
- Avoid passing source `StdUom` from new DTO into the price-resolver as a proxy for commercial selling UOM. Distinguish display UOM from stock UOM in Price Info.
- No new multi-UOM sales functionality, UOM editing, pack-size changes or conversion-table changes.

**A4 pass:** direct BOX price selection agrees with SO without any StdQty, shipment, lot, balance, cost or source consumption delta.

## A5. Currency policy for **new direct** price decisions (staged rollout)

**Files:** resolver/load context and tests; **no changes** to `CurrRate`, local amounts, customer FX rate or tax/cost calculations.

- Determine company base currency server-side from `Company.CurrencyCode`; determine effective document currency explicitly (never use blank as wildcard). Candidate blank/NULL currency means **base currency**. Item default is base-currency priced unless current source contract proves otherwise.
- Preserve explicit-currency match, precedence, date/quantity bands and existing source-chain ranking; mismatch must block with clear instructions; do not reinterpret MYR as USD.
- **No implicit currency conversion** in this plan. A planned base→foreign conversion is a separate approved feature requiring controlled FX, precision and commercial policy.
- **Compatibility requirement:** first audit existing foreign-currency customers, imports and price-list blanks. Existing linked/saved documents keep their persisted prices. Deploy new direct-pricing enforcement only after confirming no accidental interruption; otherwise mark this gate `BLOCKED` and keep Phase A1–A4 deliverable.

**A5 pass:** new USD direct sale never receives bare MYR item default; explicit USD price works; historical USD quote/SO/invoices untouched; `CurrRate` and local amount formulas unchanged.

## A6. Narrow source-fidelity and price-override hardening (requires compatibility evidence)

**Files:** `SaPriceOverridePolicy`, commercial portions of Sales `PrepareLines` + tests.

- Never infer authority from untrusted `PricingSource`, `PricingRef` or nullable client `OriginalUnitPrice` alone.
- For **new linked** DO/Invoice lines use already-loaded SO/DO source detail as commercial truth. Reject accidental unit price/discount/tax-basis mismatch from bad DTO import **only after** checking if an intentionally allowed local commercial override exists and what permission it requires. Do not overwrite source values silently in service.
- For **saved existing** drafts use their own **persisted** commercial values as reference, not today's price master. Preserve historical edits that do not change commercial values.
- For **new direct** lines, do **NOT** globally demand `UnitPrice == ResolveLinePricingAsync(...)`; current repository supports legitimate direct manual and zero prices. First classify existing caller contracts and determine a compatible explicit manual/FOC rule, then add narrowly scoped server-side validation. Without a proven rule, **leave the direct-new enforcement change BLOCKED**, while fixing source-linked validation and continuing tests.
- For source-inherited overridden prices, **no new downstream override permission**. A genuinely new local price change, if business-supported, requires destination document `PRICE_OVERRIDE` + reason and a correct persisted local baseline.
- Preserve site permissions, tenant/branch checks, concurrency tokens, source revision checks and SQL transaction semantics; do not add new lock order or isolation level to Sales saves merely for pricing.
- Do not add a price control to DO that defeats its shipping-only role. A direct DO with no price has an approved manual exception only if the existing workflow allows it and is tested.

**A6 pass:** tampered linked input blocked, authorized local change audited, inherited source price accepted, historical/direct FOC workflows not unexpectedly blocked. **Any unresolved direct-new manual-price policy is an explicit Phase A6 BLOCKER, not silently 'fixed'.**

## A7. Existing CN and price explanation UX (limited by existing schema)

- `SaCdnService.CopyFromInvoiceAsync` already copies Invoice unit price, six % discounts, the **first** amount-discount slot, tax basis and group. It currently also copies upstream original/reason. Avoid demanding override permission on that inherited value; if changing this mapping, verify original invoice provenance remains retrievable by invoice/source line.
- **Do not claim Invoice→CN fully preserves two amount slots.** The second is not in `SaCdnLineRequest`/`SaCdnDetail`. The agent must record the specific data-loss scenario and produce a separately reviewed **commercial-only schema option** (Phase B) rather than stuffing amount2 into cost/stock/accounting fields or silently compressing discount math.
- For a source Invoice with nonzero `ItemDiscAmount1`, document the current financial-risk limitation. Do not implement a new stock-return block or change `CostPrice`/return Qty without a distinct business approval.
- Strengthen Price Inquiry/Info explanations to distinguish “recorded agreed price” from “what current price rules would select today.” Respect the relevant user’s Sales visibility and menu permissions; do not apply `VIEW_PRICE` to underlying line-resolution services used by ordinary Sales entry.
- UI should display one glance: price, per commercial UOM, source, discount, and inherited/agreed state. Reuse DevExpress, existing compact transaction styles and lookups. No added mandatory clicks for ordinary partial delivery/invoice.

**A7 pass:** supported CN cases retain invoice commercial context without crossing cost/quantity; unsupported second amount slot is marked explicitly **DEFERRED/NEEDS APPROVAL**.

---

# 7. Phase B — policy / commercial-only schema decisions (**NOT AUTHORIZED FOR AUTO-IMPLEMENTATION**)

These are mandatory **decision gates**, not hidden dependencies to be patched by the agent.

### B1. DiscountMethod snapshot drift — serious correctness gap

- Problem: current `SaInvoiceCalc.CalculateLine(..., customer.DiscountMethod)` recalculates each destination with **today's** customer discount method. Source line has discount slots but no historical JOIN/SPLIT indicator.
- Example: unit RM100, discounts 10%+5%; source JOIN => RM85.00, destination sequential => RM85.50 even though both copied the same slots and same unit price.
- **Do not 'fix' by changing `SaInvoiceCalc` formulas or rewriting percentage slots**: that risks invoice tax amounts, report consistency and future regressions.
- Proposal **after separate written approval**: minimal **Sales commercial-only** per-line `DiscountMethodSnapshot` with source-line inheritance and compatibility fallback for legacy rows. Determine necessary tables from actual read paths (`SaQtDetail`, `SaSoDetail`, `SaDoDetail`, `SaInvoiceDetail`, possibly `SaCdnDetail`) and all DTOs. Mixed-source invoices can contain lines from different-era discount methods; a single invoice header method may be insufficient. Prove the minimum schema first.
- **No backfill or recalculation of posted documents.** Default legacy rows to existing customer method only when the historical value cannot be proven; explain limitation in history. New rows snapshot at the first commercial decision.
- This would affect **commercial net/tax amounts**, not FIFO/WAC/stock quantities. Re-verify posted/e-Invoice downstream and require explicit schema/security/data-migration review.

### B2. Invoice→CN second amount-discount slot

- Problem: Invoice has `ItemDiscAmount` and `ItemDiscAmount1`, but CN contract/entity currently only carries `ItemDiscAmount` (mapped to `SaCdnDetail.ItemDiscount1`).
- Proposal **after separate written approval**: add **one CN commercial amount-discount field** with explicit mapping through request, DTO, detail, UI, calculation input. However this would require careful audit of existing `SaCdnService` calculation/data mapping. **Do not touch cost-price, customer-return inventory batch creation or return quantities.**
- Without this approval, treat 2nd-slot CN copying as a known **not-fixed** case, and require manual business review of such credits. Do not describe the whole flow as 100% commercial-fidelity.

### B3. Price-override policy for direct/legacy/FOC sales

- Before enforcing against an authoritative server price for **all** new direct saves, decide whether the SME supports: ad-hoc negotiated prices without master maintenance; zero-price promotional/free samples; imports from older systems; service-only lines; manual currency prices.
- Document feature/scope distinction (new UI transactions versus legacy/import service calls). Any new strict permission boundary must be explicit and tested, not quietly hidden in `SaPriceOverridePolicy`.
- Avoid a broad “compatibility mode” or user-controlled flag that defeats authorization. If it cannot be safely enforced with current source data, leave it a separate follow-on design.

### B4. Historic tax rate and effective-date policy

- Source tax-group and inclusive flag should travel, but actual Invoice tax uses current rate. Do not freeze source tax amount by copying `TaxAmt` or adjust posted financial documents.
- Effective-dated tax-rate changes, credit-note legal tax treatment, and e-Invoice adjustments require separate tax/accounting approval. This pricing plan will not alter statutory logic.

**Phase B is not part of Phase A delivery or approval.** It can be separately scheduled after Phase A's source and pricing fixes are proven safe.

---

# 8. Required tests — commercial accuracy AND physical non-impact

## 8.1 Commercial test checklist

| ID | Mandatory test | Expected check |
|---|---|---|
| C01 | Company pricing modes × customer special/list/group/default | Priority/source eligibility unchanged. |
| C02 | Customer-item **integer** MOQ tiers, fractional order qty, list qty/date bands | Correct boundary selection; no invented fractional stored MOQ. |
| C03 | Class-specific rule A on item class B; blank rule; blank actual class | No cross-class discount; wildcard rule works; overlap-save behavior unchanged. |
| C04 | `ResolveItemDiscountAsync` fails while price is valid | Whole direct pricing request fails with message; **no silent zero discount**. |
| C05 | Direct QT/SO/DO/Invoice item selling `BOX`, standard `PCS` | Same commercial UOM and correct price; unchanged standard qty and pack. |
| C06 | Company base MYR, doc USD, blank-currency list, explicit USD row, item default | No MYR-as-USD; explicit USD wins; legacy linked prices unchanged. |
| C07 | QT accepted at RM10 → SO after master RM12 | SO keeps RM10 and discount/tax-basis fields; quote consumption unchanged. |
| C08 | SO RM10 → partial DO 40 → partial Invoice 25 after master RM12 | Frozen RM10 and slots; expected money totals; physical values unchanged. |
| C09 | Several posted DOs → one Invoice with differing line price/discount | Per-source commercial fields, quantity caps unchanged, no average pricing. |
| C10 | DO discount 10%, 5%, amount1 & amount2 → Invoice | Six % + two amount slots exactly copied; calculated by existing engine. |
| C11 | DO inclusive flag + tax group → Invoice | Source code/basis copied; destination tax calculated with existing rules; mixed basis still rejected if prohibited. |
| C12 | SO-linked Invoice when Item Master selling UOM changed after SO | Valid existing source may still fulfill with frozen pack and commercial price; no new stock conversion. |
| C13 | One source discount method JOIN, customer later switched to SPLIT | **Expected known limitation without Phase B** quantified, not claimed fixed. |
| C14 | CN from posted Invoice, first amount slot only; upstream price override | Current supported copy works and billing clerk does not need source override permission. |
| C15 | CN from posted Invoice with **second** amount slot >0 | **Expected known limitation without Phase B** quantified; no silent claim of exact commercial credit. |
| C16 | Direct Invoice price 0 when master price 8 (PACK1) | Existing legacy supported behavior preserved in Phase A; same existing StdQty update-on-master-pack-change test. |
| C17 | Direct manual price without nullable original; user without/with `PRICE_OVERRIDE` | Behavior documented; strict changes only after compatible policy approval; avoid false “pass”. |
| C18 | Malicious new linked line changes `UnitPrice`, discounts, basis, tax | Server rejects unapproved change without altering source Qty/allocations. |
| C19 | Edit unrelated field, quantity/date after line exists | No silent reprice; explicit changed-price review when needed. |
| C20 | Concurrent source revision, changed master while popup request pending, stale save | Existing locking/revision checks intact; no partial Sales save; stale response never applied. |
| C21 | Price Inquiry vs entry, `VIEW_PRICE` denied/granted | Correct company/method/UOM/currency and no unauthorized price-list exposure; Sales entry still functional. |
| C22 | Customer with foreign currency, exclusive/inclusive + discount | No implicit currency conversion, original `CurrRate`/local/tax calculations unmodified. |
| C23 | Price-group mass update or future-effective schedule while draft exists | New direct gets effective price, agreed linked/saved unchanged; master-price audit history intact. |
| C24 | Posted Invoice / CN e-Invoice mapping on changed commercial totals | Existing document e-Invoice workflow/regulatory fields unaffected except deliberate new-document correction. |
| C25 | Same customer item, different selling-UOM rows; unknown/sentinel date bands | No using wrong UOM, deterministic priority maintained. |

## 8.2 Mandatory physical and costing **negative-delta** tests

Run on **isolated SQL Server test DB with deterministic fixtures**, not production:

| ID | Scenario | What must be EQUAL before and after |
|---|---|---|
| P01 | SO → partial DO with shipment lots, source reservations | OrderQty/StdQty/StdPsize, shipped/delivered/balance/allocated, warehouse+lot, physical stock debit, WAC/FIFO result. |
| P02 | SO → direct Invoice with partial consumed qty | SO billed/consumed/remaining, Invoice stock issue StdQty, all source identity/pack fields, cost layers. |
| P03 | Multiple DO → one Invoice | Per-DO invoiced/billable caps, no duplicate source consumption, same inventory postings. |
| P04 | Direct Invoice BOX/PCS, item pack size changed before draft update | Preserve **existing** pack snapshot/refresh behavior, including test that current direct draft update changes StdQty when master pack changes. |
| P05 | ReturnStock=true CN from posted Invoice | SourceInvLine, CostPrice, warehouse/lot, CR StdQty, `IvTrxBatch`/history, `IvBalLoc`, CostLayer. |
| P06 | Post → rollback/repost DO/Invoice/CN; closed period/future date | Same allowed/blocked outcomes, stock/cost reversal balances, period controls and posting revision. |
| P07 | Concurrent duplicate posting/rollback, partial document save | Same transactions atomicity and locks; no extra rows, no negative stock, no CostLayer drift. |
| P08 | Production demand/DR/WO upstream and QA/WIP downstream | No changed production-demand quantity, WIP, FG cost or warehouse balance. |

**Equality rule:** Compare the **same accepted transaction and physical inputs** on baseline and changed code. Corrected sales **money values** may differ in specifically documented defective commercial cases. Do not treat a commercial fix as an excuse for any unexplained stock/cost/quantity delta. If stricter commercial validation blocks a previously accepted transaction, classify it as a workflow regression and obtain approval—do not hide it by excluding the transaction from comparisons.

**Test execution:** Agent must run baseline and changed-code `dotnet build ErpWeb.slnx`, focused `ErpWeb.Tests/Sales/Master`, `ErpWeb.Tests/Sales/Transaction`, and relevant Inventory, StockLedger, Costing, Production, QA, e-Invoice tests. Use project filters/truth from actual test attributes; don't invent a passing test output. SQL Server actual concurrency cases required where in-memory EF cannot verify locks. Record exact commands, duration, passed/failed/skipped counts, commit SHA and DB fixture version.

---

# 9. Gates, outputs and approval criteria

| Gate | Work | Must prove before next gate | Status now |
|---|---|---|---|
| **G0** | GPT-6 High — source maps + baseline + legacy and risk policy register | Repo/head confirmed, key tests runnable or limitations stated, physical invariants established | **READY TO RUN** |
| **G1** | Grok 4.7 High (code) + GPT-6 High (review): A1 DO→Invoice commercial DTO fidelity | Discounts/basis/OrderType copied; no quantity/cost hunk; C09–C11/P01–P03 | **BLOCKED ON G0** |
| **G2** | Grok 4.7 High (code) + GPT-6 High (review): A2 prevent inherited-price re-resolution | C07–C09/C19–C20; no physical/rollback difference | **BLOCKED ON G1** |
| **G3** | Grok 4.7 High (code) + GPT-6 High (review): A3 class + discount failure and A4 commercial UOM | C03–C05/C12; P01–P04 equal | **BLOCKED ON G2** |
| **G4** | GPT-6 High (code + separate review pass): A5 currency policy; A6 compatible override/source validation | Existing FX/manual/FOC contract signed; C06/C16–C18/C21–C23 | **CONDITIONAL** |
| **G5** | Grok 4.7 High (lead), optional Composer 2.5 (UI/tests), GPT-6 High (review): A7 UI/explanation and broad regression | UAT easy/no extra clicks; P01–P08 equal; protected diff clean | **BLOCKED ON G4 OR APPROVED DEFERRALS** |
| **B1–B4** | Optional commercial-only schema/policy items | Separate proposal, explicit approval and independent migration/regression | **NOT AUTHORIZED** |

**Gate discipline:** One gate per small PR/commit; GPT-6 High independent gate review is required before the next gate. Every gate report includes (1) current SHA and files changed; (2) **exact changed line hunks**; (3) before/after commercial cases; (4) before/after protected quantities/cost; (5) tests run, skips/failures; (6) any behavior blocked by new validation; (7) `PASS / FAIL / BLOCKED` with reason.

**Code-review checklist:**

- [ ] No edits to denylisted cost/stock/quantity/production/post/rollback files or methods.
- [ ] No `StdQty`, `StdPsize`, `IvQty`, stock-control, reservation, source-consumption, period logic changed, including through helper rewrites.
- [ ] No new lock order or transaction isolation change in Sales save/post/rollback.
- [ ] No schema migration or DML backfill, unless separately approved B gate.
- [ ] No replacement pricing engine; existing `SaCompanyPriceMethod` order, master overlap validation and `SaInvoiceCalc` math retained.
- [ ] DO source DTO transports complete commercial terms; no source Qty/cost transported as commercial calculation data.
- [ ] No new direct/legacy/FOC transaction unexpectedly blocked due to override hardening.
- [ ] Tax group and inclusive flag carried; **computed tax amount is not blindly copied**.
- [ ] `DiscountMethod` drift and CN 2nd amount slot either separately fixed with approval or explicitly listed as **not fixed**.
- [ ] Automatic source-linked repricing suppressed; line amount still recomputes on quantity change.
- [ ] Server-side linked/commercial checks compare authoritative source details and current sales edits, not client provenance tokens alone.
- [ ] Existing Sales permission controls, e-Invoice, reporting, concurrency, period and rollback tests passed or limitations documented.
- [ ] UI keeps New/Edit/Post/Rollback enabled-state logic and transaction look/feel unchanged.

**Final approval test:** Mark **APPROVED FOR IMPLEMENTATION — 10/10** only when there are no outstanding critical misstatements, G0 evidence proves this scope is safe, business-policy choices that materially change existing workflows are approved, and controlled testing can demonstrate physical/cost non-impact. **Never equate “we did not edit costing files” with proof of no costing impact.**

---

# 10. Exact AI Coding Agent handoff command

> **Run this handoff with GPT-6 High.** Connect to the **CURRENT** `mokth/net10projectTemplate` branch `productionv2`. Read this v2.1 plan and execute **G0 ONLY, no code edits**. Trace full QT→SO→DO→Invoice→CN and direct DO/Invoice flows, build exact commercial-field and protected-physical-field matrices, confirm source-line DTOs, discounts, customer DiscountMethod drift, foreign-currency pricing, manual/zero-price compatibility, and return-stock `CostPrice` path. Run a baseline test suite in isolated SQL Server fixtures and record snapshots for Qty/StdQty/StdPsize, allocations, IvBalLoc, CostLayer, FIFO/WAC, posting/rollback and e-Invoice. Mark missing data/tests **BLOCKED**, do not infer them. **No costing, stock, quantity, WIP, posting, rollback or period logic change is allowed.** Stop with an evidence report and a list of G1-ready change hunks, leaving B1–B4 as separately approved proposals.

---

# 11. Verified source index

- `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs` — pricing source chain.
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs` — base/master resolution, silent discount-fetch failure (lines 63–78), company method, price candidate queries.
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Resolve.cs` — discount-rule query and class input.
- `ErpWeb.Core/Sales/SaItemFamilyPricing.cs` — price UOM/currency source eligibility, `SaItemFamilyRuleMatch` and discount selectors.
- `ErpWeb.Core/Sales/SaPriceOverridePolicy.cs` — nullable client baseline and server validation.
- `ErpWeb.Core/Sales/SaInvoiceCalc.cs` — frozen formula (JOIN versus sequential; line and tax rounding), **DO NOT EDIT**.
- `ErpWeb.Core/Sales/ISaDoService.cs` — billable DTO missing commercial fields.
- `ErpWeb.Core/Sales/SaDoService.cs` — projected billable lines (~1615), source frozen pack (~2526), pricing source validation, posting/rollback (protected).
- `ErpWeb.Core/Sales/SaInvoiceService.cs` — SO commercial UOM master mismatch (~2295), source pack (~2332), current customer discount method (~2406), posting/rollback (protected).
- `ErpWeb.Core/Sales/SaCdnService.cs` — invoice copy (~1302), CR return `CostPrice` batch (~2355–2391), current discount method, posting/rollback (protected).
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`, `SaQt.razor.cs` — pricing/quantity/date triggers.
- `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs` — linked DO quantity triggering price re-resolution (~1212).
- `ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs` — linked Invoice quantity triggering re-resolution (~1665) and incomplete FromDeliveryOrder mapping (~2713).
- `ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs` — direct/CDN pricing triggers.
- `ErpWeb.Core/Sales/ISaCdnService.cs`, `ErpWeb.Model/Entities/Sales/SaCdnDetail.cs` — only one CN amount slot in contract/entity.
- `ErpWeb.Model/Entities/Sales/{SaQtDetail,SaSoDetail,SaDoDetail,SaInvoiceDetail}.cs` — existing commercial field persistence; **no discount-method snapshot**.
- `ErpWeb.Model/Entities/{Company.cs,Inventory/IvStockMaster.cs}` — base currency, `SellingUom`, `StdUom`, `IClassCode` versus `Classification`.
- `ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs` — `Submitted_zero_price_and_pack_gl_from_master` (direct zero-price and current physical pack-update behavior).
- `ErpWeb.Tests/Sales/Master/SaCompanyPriceMethodTests.cs`, `SaItemFamilyPricingContractTests.cs`; `ErpWeb.Tests/Sales/Transaction/SaLinePricingConsumerTests.cs`, `SaQtServiceTests.cs`, etc. — existing test contracts.

**Version note:** v2.1 adds model responsibilities, gate-by-gate review ownership and copy-ready agent prompts. All v2.0 pricing findings, protective boundaries, Phase B approvals and evidence requirements remain in force. No source code has been edited by this plan update.

**Review limitation:** This file is a deep **read-only** source review, not a test-executed implementation verification. Commercial-only Phase A is designed not to touch costing/quantity, but **zero behavioral impact to posting/stock/cost must still be proven against actual database tests at G0 and G5**.
