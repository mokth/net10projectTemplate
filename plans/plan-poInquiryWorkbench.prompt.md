# Plan: Purchase Inquiry Workbench

> **Status:** REV 3 — the external review of Rev 2 (9.4 / 10) is incorporated, and **every Rev 2 claim has been re-verified against the shipped Phase 1 code**. Three claims did not survive verification and are corrected in §4.
> **Scope:** the read-only Purchase Inquiry suite (`PO_INQUIRY`, seven screens) — exception presets ("workbench chips"), management summaries, and the OPEN navigation contract. No new documents, no schema change (one DTO field only), no new business rules.
> **Prerequisite:** Phase 1, already shipped — `plans/Procurement-Inquiry-Assessment.md` (Step 0.5 rules) and `ErpWeb.Tests/PoPurchaseInquiryServiceTests.cs`.
> **Workspace:** `c:\wincom\net10projects` — ErpWeb.slnx, .NET 10, Blazor + DevExpress, EF Core, SQL Server (tests on SQLite).

## 0. Change log

| Rev | Date | Change |
|---|---|---|
| 1 | 2026-09-24 | Initial workbench plan — PO Outstanding presets + summaries. |
| 2 | 2026-09-25 | External review round 1 (9.4/10); P0/P1 items folded in. |
| 3 | 2026-09-26 | Review round 2 re-verified claim-by-claim against the code. Corrections: the OutstandingValue STOP rule (§4.1), the EF-translatability fallback (§4.2), the "presets are dead code" premise (§4.3). Added Phase 1 compatibility rule (§16), regression baseline (§17), the seven-screen scope table (§3), the PO revision-fidelity fix (§14), preset validation (§13), and the materialisation-exception register (§6). |

## 1. TL;DR

Phase 1 gave Purchase seven read-only listings. This plan turns **three of them into decision screens** (PO Outstanding, PR Status, Self-billed e-Invoice) by adding *exception presets* ("chips") that narrow the same grid the listing already renders, plus a management summary strip driven by the *same* query pipeline. The workbench adds **no new document, no new accounting formula, and no new status vocabulary** — every preset is a direct comparison against a persisted column, and every summary figure is already computed by the Phase 1 service.

**Delivered so far (verified):** the preset plumbing, the canonical pipeline, three screens with chips + summaries, and the workbench test class (§2).

**Remaining:** preset/summary coverage for the other four screens (subject to D1–D3), the PO revision-fidelity fix in `PoInquiryNavigation` (§14), preset-value validation (§13), and the two documented materialisation exceptions (§6).

## 2. Verified current state — do not re-research

Everything below was read from the repository on 2026-09-26. Treat it as fact.

| Area | State | Evidence |
|---|---|---|
| Preset keys | Shipped — `ALL_OPEN`, `OVERDUE`, `PARTIAL`, `UNBILLED`, `UNCONVERTED`, `SB_NOT_SUBMITTED`, `SB_INVALID` | `PoInquiryResults.cs:37-51` |
| Preset transport | Shipped — `PoInquiryQuery.WorkbenchPreset` | `PoInquiryResults.cs:27` |
| Canonical builders | Shipped — `BuildPoOutstandingSlice(…, applyPreset)` and `BuildPrStatusSlice(…, cancel, applyPreset)` | `PoPurchaseInquiryService.cs:80, 191, 418` |
| PO Outstanding chips | Shipped — 4 chips + summary strip | `PoOrderOutstandingInquiry.razor:30-43`, `.razor.cs:12-13, 63, 78-100, 111-112` |
| PR Status chip | Shipped — `UNCONVERTED` + summary strip | `PoPrStatusInquiry.razor:30`, `.razor.cs:58, 73-89, 101` |
| SB e-Invoice chips | Shipped — `SB_NOT_SUBMITTED`, `SB_INVALID` + summary strip | `PoSbEInvoiceInquiry.razor:31-35`, `.razor.cs:84-100, 117-120` |
| Preset UI plumbing | Shipped — `WorkbenchPreset`, `TogglePresetAsync`, `ChipClass`, preset carried into the CSV URL | `PoInquiryPageBase.cs:32, 103, 187-204, 243` |
| Workbench tests | Shipped — 5 facts (PO presets + value, summary ACCESS parity, PR unconverted + linked POs, supplier monthly net, SB presets) | `ErpWeb.Tests/PoPurchaseInquiryWorkbenchTests.cs` |
| Phase 1 tests | 12 facts | `ErpWeb.Tests/PoPurchaseInquiryServiceTests.cs` |
| Navigation tests | Shipped | `ErpWeb.Tests/PoInquiryNavigationTests.cs` |
| CSV exports | 9 routes; refuse-don't-truncate above the cap | `ErpWeb/Purchase/PoInquiryExportEndpoints.cs:23-34, 390-400` |
| Revision base | `PoRelNo` is **1-based** (first revision = 1; revisions `+1`) | `PoOrderService.cs:492, 684, 1114` |

**Not yet workbench-ised (no chips, no summary strip):** Supplier Transactions (informational period chips only), Purchase Invoices, Credit/Debit Notes, Document Relationships.

## 3. Scope — the seven Phase 1 screens

This list is the exact contents of `KnownMenus` (`PoPurchaseInquiryService.cs:19-27`). A screen absent from that set fails closed with ACCESS denied — that is by design, not a bug.

| # | Screen | Route | Menu code | Workbench preset |
|---|---|---|---|---|
| 1 | PO Outstanding | `/purchase/inquiry/po-outstanding` | `PO_ORDER_OUTSTANDING` | `ALL_OPEN` / `OVERDUE` / `PARTIAL` / `UNBILLED` |
| 2 | PR Status / Outstanding | `/purchase/inquiry/pr-status` | `PO_PR_STATUS` | `UNCONVERTED` |
| 3 | Supplier Transactions | `/purchase/inquiry/supplier` | `PO_SUPP_TRX` | *(period totals only — D1)* |
| 4 | Purchase Invoices | `/purchase/inquiry/invoices` | `PO_INV_INQUIRY` | *(D2)* |
| 5 | Credit / Debit Notes | `/purchase/inquiry/credit-debit-notes` | `PO_CDN_INQUIRY` | *(D2)* |
| 6 | Document Relationships | `/purchase/inquiry/doc-relationships` | `PO_DOC_REL` | *(navigation only — D3)* |
| 7 | Self-billed e-Invoice | `/purchase/inquiry/einvoice` | `PO_SB_EINV_INQUIRY` | `SB_NOT_SUBMITTED` / `SB_INVALID` |

Menu seed triad: `ErpWeb.Core/Menus/MenuCodes.cs:203-211` + `ErpWeb/Menus/menus.xml` + `scripts/init-po-inquiry-menu.sql` (guarded by `MenuDeploymentParityTests`).

## 4. Corrections to the Rev 2 review

Three review items could not be supported by the code. Apply these in place of the Rev 2 wording.

### 4.1 OutstandingValue — the formula exists; do not STOP

Rev 2 said: *"If no authoritative existing outstanding-value calculation can be identified, STOP and report the missing business rule rather than inventing a formula."*

That instruction is now **wrong**, because the authoritative implementation already ships:

```
OutstandingValue = PoOrderCalc.RoundMoney(NetAmount × BalanceQty / PoPurQty)   // 0 when PoPurQty <= 0
```

Evidence: `PoPurchaseInquiryService.cs:255-257` (row) and `:163-167` (chip sum). The rationale is recorded on the DTO at `PoInquiryResults.cs:76-82`: line `NetAmount` is the post-discount commercial net (`PoOrderCalc.ApplyTwoLevelDiscount`), PO `TotAmnt` is `Sum(NetAmount + TaxAmount)`, therefore proportional allocation by `BalanceQty / PoPurQty` is the only figure consistent with the PO list.

**Rule:** lock the expression above verbatim. Forbidden alternatives: `BalanceQty × PoUnitPrice`, tax-inclusive `TotAmnt`, re-applying discounts, UOM conversion, proportional tax, FX restatement. STOP only if a requirement needs an outstanding figure *different* from this one, or if the proportional rule fails on a real document.

### 4.2 `ComputeInvoiceable()` is not EF-translatable — the fallback already exists

`PoOrderCalc.ComputeInvoiceable` (`PoOrderCalc.cs:45-46`) is a plain static using `decimal.Round(…, 4, AwayFromZero)`. EF cannot translate it. The codebase's established pattern is:

1. **In the query:** inline the arithmetic, relying on `Max(0, x) > 0 ⟺ x > 0` — exactly as `BuildPoOutstandingSlice` does (`(RecvQty - ReturnQty - InvoicedQty) > 0`).
2. **After materialisation:** call `PoOrderCalc.ComputeInvoiceable` on rows only (`MapPoOutstandingRow`).

Never materialise the full result set to evaluate it. If a future aggregate needs the *rounded* invoiceable quantity rather than a boolean, note that a SQL `SUM` differs from `SUM(ComputeInvoiceable)` for quantities beyond 4 decimals. If neither route satisfies the requirement, STOP and report — do not add a client-evaluation fallback.

### 4.3 Presets are not dead code — the remaining gap is the other four screens

Rev 2's premise that preset plumbing must be introduced is stale. It is shipped end-to-end, including the CSV round-trip (`PoInquiryPageBase.cs:243`). The real remaining gap is §3 rows 3–6, plus the defects in §13–§15.

## 5. Implementation guardrails (binding)

1. **No new business rules.** Every preset must be a direct comparison against an existing persisted column. A preset that needs a new definition is a D-item first.
2. **Tenant scope first, then ACCESS**, via `GateAsync` — the existing order. Never resolve ACCESS before tenant scope.
3. **Every service method takes `menuCode` first**; every summary method calls the same gate as its grid.
4. **One bounded query per aggregate.** No N+1, no per-row lookups; batch lookups only.
5. **Read-only.** Never mutate transactional data, never recompute a persisted rollup for display.
6. **CSV calls the same service method with the same applied filter** and refuses (never truncates) above the cap — `PoInquiryExportEndpoints.cs:390-400`.
7. **Persisted values show as stored** — `PoPurQty`, `RecvQty`, `ReturnQty`, `InvoicedQty`, `BalanceQty`, `TotAmnt`. Derived values are labelled derived.
8. **Company-local "today"** comes from `ICurrentDateService` (`PoInquiryPageBase.BuildQuery()` → `AsOfDate`), never `DateTime.Today` in the UI.
9. **Reuse the existing decoration** for e-Invoice status (`EInvoiceStatuses.Normalize` + `DecorateEInvoiceRowsAsync`). Never add a second status map.
10. **Preset vocabulary is UI-only** — never persisted, never a column, never a status.

## 6. Canonical filter pipeline (single source of truth)

```
query → GateAsync (tenant → ACCESS) → Build…Slice (base filters → latest revision → applyPreset) → grid | summary | CSV
```

* `BuildPoOutstandingSlice(db, company, branch, query, applyPreset)` and `BuildPrStatusSlice(db, company, branch, query, cancelled, applyPreset)` **are** the applied dataset. Do not add a second query path and do not rename them to `BuildFilteredQuery`.
* The **grid** calls the builder with `applyPreset: true`. The **summary** calls the *same* builder with `applyPreset: false`, so chips remain an exception dashboard and never inherit the active preset (`PoPurchaseInquiryService.cs:121-127`; enforced by the UI clearing the preset on the summary query — `PoOrderOutstandingInquiry.razor.cs:83-86`).
* Consequence to keep true: **with preset P active, grid `TotalCount` equals chip P's count.** Covered by `PoPurchaseInquiryWorkbenchTests.PoSummary_And_Presets_Overdue_Partial_Unbilled_Value`.
* Any new screen follows the identical shape: one builder, two `applyPreset` modes, gate in every entry point.

### 6.1 Materialisation-exception register

"No full-grid materialization" applies to **new** workbench queries. Two Phase 1 paths deliberately materialise; do **not** silently "fix" either under this rule (§16 applies):

| Path | Behaviour | Status |
|---|---|---|
| `GetDocumentRelationshipAsync` | Materialises all five unions into a list, then filters and pages in memory (`PoPurchaseInquiryService.cs:1035-1120`) | Pre-existing Phase 1 deviation — log as a known issue; changing it is a separate, explicitly approved change. |
| `GetSbEInvoiceStatusAsync` / `GetSbEInvoiceSummaryAsync` | Materialises the filtered set when an SB preset is active or for the summary, because presets require post-decoration filtering (`:1201-1213`) | Documented and intentional; keep, but bound. Must not become the pattern for new screens. |

## 7. Locked semantics (used everywhere)

| Term | Definition | Source |
|---|---|---|
| `ALL_OPEN` | default arm — `BalanceQty > 0` | `PoPurchaseInquiryService.cs:232-239` |
| `OVERDUE` | `BalanceQty > 0 && EtaDate != null && EtaDate < asOf` — **ETA equal to today is not overdue** | `:232-233`; test PO2 |
| `PARTIAL` | `BalanceQty > 0 && RecvQty > 0` — **line-level** | `:234-235` |
| `UNBILLED` | `(RecvQty − ReturnQty − InvoicedQty) > 0` — **independent of `BalanceQty`**; may include lines with `BalanceQty = 0` | `:236-237` |
| `UNCONVERTED` | `PurchaseQty − Σ(non-cancelled PO `PoPurQty` for this `PrNo`/`PrLineNo`) > 0` | `:455-470` |
| `SB_NOT_SUBMITTED` | no registry row **and** `IrbmStatus` blank (after decoration) | `DecorateEInvoiceRowsAsync` |
| `SB_INVALID` | normalized latest submission or `IrbmStatus` ∈ {`INVALID`, `REJECTED`} | `IsSbInvalidRow`, `:1264-1269` |
| `OutstandingValue` | `RoundMoney(NetAmount × BalanceQty / PoPurQty)`, `0` when `PoPurQty <= 0` | `:255-257` |
| `InvoiceableQty` | `Max(0, RecvQty − ReturnQty − InvoicedQty)` — derived, never persisted | `PoOrderCalc.cs:45-46` |
| Live PO consumption | Σ `PoOrderDetail.PoPurQty` over **all** non-cancelled `PoRelNo` rows — not the max revision | `PoOrderService.LivePoConsumedForPrAsync` |
| Supplier net | `POSTED INV (Type = Invoice only) + POSTED PoCdn DN − CN`; positive CN/DN; no qty-CN; no self-billed | `PoPurchaseInquiryService.cs:920-1005` |

### 7.1 UNBILLED is intentionally `BalanceQty`-independent

State this explicitly in code comments and tests, because it is the single most likely place for an agent to "helpfully" add `BalanceQty > 0`:

* `ALL_OPEN` is the **only** preset that filters on `BalanceQty`.
* `UNBILLED` is `ComputeInvoiceable() > 0` — its purpose is to catch *fully received but not invoiced* lines, which by definition have `BalanceQty = 0`.
* Chip and grid use the same predicate (`:163-165` chip, `:236-237` grid).
* Regression guard: `PoPurchaseInquiryWorkbenchTests` PO4 (ordered 8, balance 0, received 8, invoiced 0) must count as unbilled.

### 7.2 PARTIAL — decision process (resolved)

1. Search for an existing helper. **Done.** `PoOrderCalc.HasRemainingBalance` and `PoOrderCalc.AnyReceived` are header/aggregate-level (`PoOrderCalc.cs:96-100`) and cannot express a line-level partial.
2. No line-level helper exists → the locked rule is `BalanceQty > 0 && RecvQty > 0`.
3. Do **not** confuse it with `PoOrder.razor.cs` `HasPartialInvoice` (`InvoicedQty > 0 && InvoiceableQty > 0`) — partial *invoice*, not partial *delivery*.
4. Record the search evidence in a test name and a code comment so a later reviewer does not re-litigate it.

## 8. PO Outstanding workbench

* Chips: `ALL_OPEN`, `OVERDUE`, `PARTIAL`, `UNBILLED`; clicking the active chip clears back to `ALL_OPEN` (`TogglePresetAsync(preset, PoInquiryWorkbenchPresets.AllOpen)`).
* Summary strip: open lines, overdue, partial, unbilled, outstanding value — computed with `applyPreset: false`, so every chip stays visible while one is active.
* OPEN uses the row's exact `PoNo + PoRelNo` (§14).
* Ageing is **not** in this plan (Rev 2 did not scope it; the sales-side `SA_MONITOR` plan owns ageing vocabulary). If wanted later, it is a new D-item.

## 9. PR Status workbench

* Chip: `UNCONVERTED`; default preset for the screen is `UNCONVERTED` (`PoPrStatusInquiry.razor.cs:58`).
* Summary: unconverted line count + total remaining qty, computed from the same builder with the preset applied to a *cloned* query (`:392-405`).
* `LinkedPoNos` comes from a single batched lookup (`AttachLinkedPoNosAsync`) — keep it batched; never per-row.

## 10. Supplier Transactions & History

* Tab A: server-side paged union of PR / PO (all revisions) / INV / QtyCN / CN / DN / SBI / SBC / SBD, with `PoRelNo` carried for POs.
* Tab B: monthly history from `GetSupplierPurchaseHistoryAsync` — POSTED INV only (qty-CN excluded by `Type = Invoice`), POSTED PoCdn, `Net = INV + DN − CN`, self-billed excluded. Totals come from `GetSupplierPurchaseHistoryTotalsAsync`.
* Amounts reuse the list conventions (PR line nets + taxes; PO `Sum(NetAmount + TaxAmount)`) — no second accounting formula.
* No preset today. **D1.**

## 11. Purchase Invoices and Credit/Debit Notes

Raw listings with OPEN; no preset, no summary strip today. **D2.**

If approved, the only candidates that satisfy guardrail §5.1 (direct check of a persisted column) are:

| Screen | Proposed preset | Predicate | Why it is safe |
|---|---|---|---|
| Purchase Invoices | `NOT_POSTED` | `Status != POSTED` | Persisted `Status`; finds documents that never reached the ledger. |
| Credit / Debit Notes | `RETURN_STOCK` | `ReturnStock = true` | Persisted flag linking the CN to a vendor return. |

Anything richer (ageing, exposure, netting) needs AR/AP data the module does not have — keep it out.

## 12. Document Relationships

* Reads five relations: PR→PO, PO→GR (`IvTrxBatchDetail` where `TrxType` ∈ {GR, NG}), PO→INV, INV→CDN, INV→QtyCN.
* OPEN SOURCE / OPEN TARGET with the unavailable path handled by `PoInquiryNavigation`.
* **Gap:** `PoDocumentRelationshipRow` (`PoInquiryResults.cs:200-212`) has **no `PoRelNo`**, so the screen physically cannot open a specific PO revision. Fix in §14.

## 13. Preset validation (new)

* Unknown preset values currently fall into the `_ =>` arm and silently behave as `ALL_OPEN` while the UI implies a filter. **Reject** unknown values at the boundary instead of defaulting.
* `ALL_OPEN` must be an explicit, named arm, not just the default fall-through.
* Add a test per screen asserting: (a) each preset's grid count equals its chip count; (b) an unknown preset is rejected, not silently widened.
* The preset must continue to round-trip through `BuildExportUrl` so CSV matches the grid.

## 14. Navigation contract

**Rule:** OPEN must preserve the row's exact `PoNo + PoRelNo` and must never re-query the latest revision. Target-page authorization is unchanged — inquiry OPEN must not bypass document-level ACCESS.

1. **Fix the confirmed defect.** `PoInquiryNavigation.TryResolveRelationship` passes `poRelNo: null` for PR→PO, PO→INV and PO→GR source-open (`PoInquiryNavigation.cs:160, 162, 171`), so an older-revision row opens the **latest** revision. Extend the signature to accept and forward the revision; add a failing-then-passing test.
2. **Carry the revision into the row.** Add `PoRelNo` to `PoDocumentRelationshipRow` and populate it from `PoOrderDetail.PoRelNo` (PR→PO), `PoInvoiceDetail.PoRelNo` (PO→INV) and `IvTrxBatchDetail.PoRelNo` (PO→GR). Without this, step 1 has nothing to forward. *This is the only schema-adjacent change in the plan — a DTO field, not a column.*
3. `TryResolveByDocType("PO", docNo, poRelNo)` already forwards the revision correctly (`:127`) — use it as the model. `PoSupplierTransactionRow.PoRelNo` is `short?` and the supplier union spans **all** revisions, so it carries the true revision for old rows.
4. `TryResolvePo` keeps its `poRelNo is > 0` guard: revisions are 1-based (`PoOrderService.cs:684`), so revision 0 does not occur and the guard is safe. **Do not** "fix" it to `>= 0`.
5. Blank or unknown `DocType` keeps today's behaviour: return `false` → disable OPEN and show `Document unavailable`. Never invent a route from an unknown type.
6. `PoOrderOutstandingRow.PoRelNo` is a non-nullable `short`; never map it to `null` on the way to navigation.

## 15. Summary performance contract

* Grid paging and summary aggregation run on the same filtered `IQueryable` **before** paging. Never derive a summary from the current grid page.
* Aggregates are server-side `COUNT` / `SUM`; no full-grid materialisation except the §6.1 register.
* Summary ACCESS must equal grid ACCESS (`PoPurchaseInquiryWorkbenchTests.PoSummary_AccessDenied_MatchesGrid`).
* Summaries are cheap to call twice (grid `TotalCount` + strip), but each call must stay bounded — `GetSbEInvoiceSummaryAsync` loading the whole filtered set is acceptable only for the SB screen and must not spread.

## 16. Phase 1 compatibility rule

The workbench edits **already-shipped** code (`PoPurchaseInquiryService`, `IPoPurchaseInquiryService`, `PoInquiryNavigation`, `PoInquiryExportEndpoints`, `PoInquiryPageBase`, the seven `ErpWeb.UI/Purchase/Inquiry` pages, `PoInquiryResults`). It must not change existing:

* filtering semantics (base filters, half-open dates, search fields)
* ACCESS semantics (`GateAsync` stays the only gate; summaries stay as guarded as their grids)
* latest-revision selection (`MAX(PoRelNo)` per `PoNo`)
* paging / sorting order
* CSV output shape, cap and refuse-don't-truncate behaviour
* the supplier-history net rule
* e-Invoice decoration and `EInvoiceStatuses.Normalize` mapping

If supporting the workbench requires changing any of the above, **stop and identify the change explicitly** before implementing it.

## 17. Regression baseline

```
Before implementation:
    dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"
    → record the passing/failing counts.
    Declare green: PoPurchaseInquiryServiceTests (12), PoPurchaseInquiryWorkbenchTests (5),
                   PoInquiryNavigationTests, and the PO/PR list suites they touch.

After each vertical slice:
    Full "Category!=SqlServer" run — Phase 1 suite plus the new slice tests.
    A slice is done only when the pre-existing failure set is unchanged.

At the end:
    Full regression + dotnet build ErpWeb.slnx --nologo -v:q (0 errors — Razor only compiles here).
```

New tests passing is not evidence unless the pre-existing suite is still green.

## 18. Acceptance criteria

| # | Criterion |
|---|---|
| A1 | Summary ACCESS = grid ACCESS on every screen; a denied caller cannot read a chip the grid refuses. |
| A2 | Grid `TotalCount` equals the active chip's count for every preset. |
| A3 | `UNBILLED` includes `BalanceQty = 0` lines with invoiceable qty > 0, and excludes them only when invoiceable is 0. |
| A4 | `PARTIAL` is line-level (`BalanceQty > 0 && RecvQty > 0`) and is not confused with partial-invoice. |
| A5 | `OutstandingValue` matches `NetAmount × BalanceQty / PoPurQty` on rows and in the chip sum; no alternative formula anywhere. |
| A6 | OPEN preserves `PoNo + PoRelNo` from the row, including relationship OPEN; OPEN cannot bypass target authorization; unknown/blank document disables OPEN. |
| A7 | Grid and summary use the same canonical builder; no second filter implementation exists. |
| A8 | No full-grid materialisation outside the §6.1 register. |
| A9 | Unknown preset values are rejected, not silently widened to `ALL_OPEN`. |
| A10 | CSV output matches the grid for the active preset and refuses above the cap. |
| A11 | Seven screens still enumerated by `KnownMenus`; an eighth screen fails closed. |
| A12 | Phase 1 tests unchanged and green. |

## 19. Open decisions

| # | Decision | Recommendation |
|---|---|---|
| **D1** | Supplier Transactions preset | **None.** Tab B period totals already give the management view; a supplier "exception" preset would need AP data the module lacks. |
| **D2** | Invoice / CDN presets | **Add `NOT_POSTED`** (invoices) and **`RETURN_STOCK`** (CN/DN) only — both read persisted columns. |
| **D3** | Document Relationships preset | **None**; scope this screen to the revision-fidelity fix (§14). |
| **D4** | Ageing buckets on PO Outstanding | **Out of scope** — owned by the sales-side monitor plan; revisit as a separate D-item. |
| **D5** | Bound for the SB materialisation | **Propose** a documented cap; confirm the number before implementing. |

## 20. Build order

`Preset validation (§13) → PO revision fidelity (§14) → remaining presets per D1–D3 → materialisation register (§6.1)` — each independently verifiable (build + its test class green), so work can stop after any step.

## 21. Review traceability

| Rev 2 item | Where it landed | Note |
|---|---|---|
| 1 Canonical filter pipeline | §6 | Method names corrected to the shipped `Build…Slice` builders. |
| 2 UNBILLED independent of `BalanceQty` | §7.1, A3 | Confirmed already true in code. |
| 3 PARTIAL decision process | §7.2 | Resolved: no line-level helper exists. |
| 4 OutstandingValue STOP rule | §4.1, A5 | **Reversed** — the formula ships; lock it instead. |
| 5 PR unconverted | §9 | Confirmed; keep batched `LinkedPoNos`. |
| 6 Supplier history net | §10 | Confirmed; keep single net rule. |
| 7 SB e-Invoice status reuse | §5.9, §12 | Confirmed; keep single decoration. |
| 8 PO OPEN revision fidelity | §14 | Verified as a real defect, and it needs a DTO field to be fixable. |
| 9 Summary performance | §15 | Plus the §6.1 exception register. |
| 10 Acceptance criteria | §18 | Kept A1–A4, A7, A8 verbatim in intent; extended. |
| 11 Phase 1 compatibility rule | §16 | Added. |
| 12 Regression baseline | §17 | Added, with the real test classes named. |
| 13 Seven-screen scope | §3 | Added, with the `KnownMenus` invariant. |
| P0-1 UNBILLED | §7.1 | Done. |
| P0-2 OutstandingValue STOP | §4.1 | Done, corrected. |
| P0-3 `ComputeInvoiceable` fallback | §4.2 | Done, with the existing pattern. |
| P1-1..4 | §16, §17, §3, §14 | Done. |
| *(new)* Preset validation | §13 | Not in Rev 2. |
| *(new)* Materialisation exceptions | §6.1 | Not in Rev 2. |
