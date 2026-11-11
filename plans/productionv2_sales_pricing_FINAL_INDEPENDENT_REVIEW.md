# Sales Pricing Flow — Final independent review (v2.1 §0.1 / §9)

**Decision:** **BLOCKED — not approved for merge, release, or 10/10.**  
**Phase A commercial code (A1–A5, A7) on this uncommitted diff:** **no blocking defect found.**  
**A6:** **BLOCKED** (approved deferral; not implemented).  
**Phase B (B1–B4):** **NOT AUTHORIZED** (not implemented).  
**Costing skill status:** **PARTIAL** (Sqlite commercial + a few physical snapshot tests; SQL Server P01–P08 not run).

**Reviewer:** Cursor Grok 4.6, substituting for the plan’s named “GPT-6 High”. Substitution does not waive evidence.  
**Date:** 10 November 2026  
**Class:** B costing-adjacent. This review inspected **current local uncommitted diffs**, not the v2.1 plan score and not the implementer gate write-ups as proof.

This is not a 10/10 implementation approval. “No costing files edited” is not proof of no costing impact.

---

## 1. What was reviewed

| Pin | Value |
|---|---|
| Local HEAD | `e24f54069316a346171fed7f14017c09d593cb95` (`productionv2`) |
| Origin `productionv2` | `584411e317ac6990ed8a5f0886207779d821deaf` — **14 commits ahead**, all Inventory/StockLedger/Production WAC/FIFO replay plus **test** edits in `SaInvoiceServiceTests.cs` / `SaDoServiceTests.cs` |
| Working tree | G1–G5 **uncommitted**. New: `SaLinePricingTrigger.cs`, `SaInvoiceFromDeliveryOrderTests.cs`, `SaLinePricingTriggerTests.cs`, G0–G5 plan artifacts |
| Schema / migrations | **None** in this diff |
| SQL Server scratch | `ConnectionStrings__SqlServerTestConnection` **unset**. `ERPWEB_REQUIRE_SQLSERVER_TESTS` **unset** |
| Browser UAT | **Not run** |

v2.1 §0.2 requires CURRENT `productionv2` before each gate. G0 evidence was taken from origin `584411e`. **G1–G5 were implemented on older local HEAD `e24f540`.** Origin Core Sales production files did not move between those SHAs; origin **did** change Invoice/DO **tests** and a large costing-replay surface. Merge as-is would overlay stale tests onto newer costing tests.

Per-gate independent checkpoints (G1→G2→G3→G4→G5) were **not** performed between implementations. This is the first independent pass over the whole Phase A diff.

---

## 2. Protected-file / denylist scan

`git diff` vs `e24f540` for these paths is **empty**:

- `ErpWeb.Core/Sales/SaInvoiceCalc.cs`
- `ErpWeb.Core/Sales/SaPriceOverridePolicy.cs`
- `ErpWeb.Core/Inventory/**`
- `ErpWeb.Core/StockLedger/**`
- `ErpWeb.Core/Production/**`
- `ErpWeb.Model/**`

Sales **service** diffs that *exist* are commercial-only hunks:

| File | Hunk (this review) | Protected methods |
|---|---|---|
| `ISaDoService.cs` / `SaDoService.SearchBillableLinesCoreAsync` | Adds six %, both amount slots, `IsInclusive`, `TaxGroup`, `OrderType`, display `StdUom` from **saved `SaDoDetail`** | Post/rollback/`BeginTransaction`/frozen pack **not in this diff** |
| `SaInvoiceService.cs` | Deletes the SO-line **master SellingUom mismatch** error; still assigns `sellingUom = soDetail.SellingUom`. Frozen pack still via `SaProductionDemandRules.ResolveFrozenStdFactor` | PrepareLines pack, Post, Rollback **unchanged** |
| `SaCdnService.CopyFromInvoiceAsync` | Nulls `OriginalUnitPrice`/`OverrideReason`; warns when invoice `ItemDiscAmount1 != 0` | Return-stock `CostPrice` / `IvTrxBatch` **not edited** |
| `SaItemFamilyPricing.cs` / `PriceSources.cs` | Directional class match; server `IClassCode`; Qty≤0 skip; fail-closed discount lookup; company-base currency | No `CurrRate`, no pack, no `IvQty` |
| UI QT/SO/DO/Invoice/CN | Live-resolve skip + `CommercialUom`; Price Info recorded vs current-rule | Recalc still `SaInvoiceCalc` |

`StdQty` / `IvQty` / `CostLayer` / `BeginTransaction` still appear in those Sales services as **pre-existing** post/prepare paths. They are **not** in the G1–G5 hunks.

---

## 3. A1–A7 vs implemented code

| ID | Plan intent | Independent finding |
|---|---|---|
| **A1** | DO→Invoice commercial DTO | **Met.** `FromDeliveryOrder` copies six % + both amounts + inclusive + tax group + OrderType; **does not** copy Amount/TaxAmt/NetAmount; nulls override; picker then `RecalcDocument()`. Per-DO terms kept (C09-style unit test). |
| **A2** | No live reprice on inherited/saved/override | **Met for DO/Invoice/CN** via `SaLinePricingTrigger.AllowLiveResolve` + document keys (not `PricingSource` tokens). QT/SO skip on `_editingLine` / override (QT→SO conversion is a **server snapshot import**, not the live resolver). Popup amounts still follow `PopupCalc` / `RecalcDocument` on Apply. |
| **A3** | Authoritative class; fail discount lookup; directional match | **Met on live `ResolveLinePricingAsync`.** Server `IvStockMaster.IClassCode`; `request.IClass` ignored there. `ClassesOverlap` still used at save-time. Failed lookup/`Data==null` fails the whole price request. Direct `ResolveItemDiscountAsync` still takes caller `IClass` (test/API only). |
| **A4** | Commercial UOM only; frozen pack | **Met.** Resolver UOM = `CommercialUom(SellingUom, StdUom)`. Invoice SO-link no longer blocks on master UOM drift. Frozen pack path intact. |
| **A5** | Blank = company base; no FX | **Met.** `CurrencyMatches` uses `Company.CurrencyCode` loaded server-side. Missing/blank base fails closed. Item default is base. No `CurrRate` conversion. |
| **A6** | Compatible server override / linked-source authorization | **Not implemented. Remains BLOCKED.** `SaPriceOverridePolicy` **unchanged**. C18 tampered linked request is **not proven**. |
| **A7** | CN copy + Price Info | **Met with C15 limitation.** CN does not re-declare invoice override. Amount2 cannot be stored; warning on copy. Price Info distinguishes recorded agreed vs current-rule check. `VIEW_PRICE` does not gate line resolve (C21). |

**Known not-fixed (Phase B / deferrals — do not claim fixed):**

- **C13** `SaCust.DiscountMethod` JOIN/SPLIT drift — live customer method at calc time.
- **C15** CN has no second amount-discount slot.
- **C17/C18** strict override / tampered linked commercial fields.
- **Qty ≤ 0** on live resolve: G3 amendments asked for a pricing **error**; implementation **skips** the discount query and returns `None`. Popup save already refuses Qty ≤ 0. Residual, not a posted-money defect.

**Intentional operator impact (A5):** a company with blank `CurrencyCode` cannot live-price a new-direct line. A USD document no longer receives a blank/MYR list or item-default as if it were USD. Linked/saved lines are not re-resolved (G2).

---

## 4. Tests this review actually ran

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaCdnServiceTests|FullyQualifiedName~SaPriceInfoPresentationTests|FullyQualifiedName~SaLinePricingConsumerTests|FullyQualifiedName~SaItemFamilyPricingContractTests|FullyQualifiedName~SaCompanyPriceMethodTests|FullyQualifiedName~SaItemFamilyResolutionServiceTests|FullyQualifiedName~SaLinePricingTriggerTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~C12_so_linked|FullyQualifiedName~Linked_do_invoice_inherits_commercial|FullyQualifiedName~Submitted_zero_price_and_pack_gl_from_master|FullyQualifiedName~Pure_LinkDo_invoice_AddShipment_creates_no_batch"
```

**284 passed, 0 failed, 0 skipped.** Sqlite. Duration ~36s. SHA `e24f540` + local uncommitted G1–G5.

Physical cases inside that filter (`Submitted_zero_price_and_pack_gl_from_master`, `Pure_LinkDo_invoice_AddShipment_creates_no_batch`, existing CN `ReturnStock_*`) were **not loosened**. That is **not** isolated SQL Server P01–P08.

Full Sales suite, Inventory/StockLedger/Production, e-Invoice, and origin’s new WAC receipt-replay tests: **not re-run on this tree** (tree is behind those origin commits).

---

## 5. v2.1 §9 checklist

| Checklist item | Result |
|---|---|
| No denylisted cost/stock/qty/production/post/rollback files | **PASS** on this diff |
| No `StdQty`/`StdPsize`/`IvQty`/reservation/source-consumption formula change | **PASS** on inspected hunks |
| No new lock order / isolation in save/post/rollback | **PASS** (no such hunk) |
| No schema migration | **PASS** |
| No replacement pricing engine; `SaInvoiceCalc` retained | **PASS** |
| DO DTO complete commercial terms; no pack used as price | **PASS** |
| No unexpected FOC/manual block from override hardening | **PASS** (A6 not done) |
| Tax group + inclusive copied; computed tax not the source of truth | **PASS** on Invoice picker (amounts omitted). CN copy still **transports** invoice TaxAmt/Amount then **RecalcDocument** overwrites |
| DiscountMethod drift and CN amount2 listed as not fixed | **PASS** (explicit) |
| Source-linked reprice suppressed; amounts still recompute | **PASS** (`PopupCalc` + Apply `RecalcDocument`) |
| Server-side linked checks vs authoritative source, not tokens | **FAIL / deferred with A6** — C18 not implemented |
| Permissions / e-Invoice / concurrency / period / rollback | **UNVERIFIED** at this SHA vs origin |
| UI New/Edit/Post/Rollback look-and-feel | **UNVERIFIED** (no browser) |
| SQL Server P01–P08 physical equality | **UNVERIFIED** |

---

## 6. Mandatory C/P coverage (honest)

| IDs | Status |
|---|---|
| C01–C06, C12, C14, C19 (trigger), C21 | Covered by the 284-test Sqlite filter |
| C07 QT→SO keep price | Conversion is server snapshot (unchanged by this diff). UI skip on SO is `_editingLine`, not `QtNo`. **Not a dedicated C07 save test in this filter.** |
| C08–C11 | Partial (DTO/trigger/unit tests). Not a posted partial-DO/Invoice money+stock SQL case |
| C13, C15 | Documented **not fixed** |
| C16 | Existing `Submitted_zero_price_and_pack_gl_from_master` still in filter |
| C17–C18, C22–C23 | **Not opened** (A6 blocked; no FX conversion added) |
| C20, C24, C25 | **Not independently re-proven** |
| P01–P08 | **UNVERIFIED** |

---

## 7. Fix list before this work can be approved

Do **not** re-implement G1–G5 for a commercial logic defect — none was found. Do **not** start A6 or Phase B unless separately approved.

1. **Rebase** the uncommitted G1–G5 work onto current `origin/productionv2` (`584411e` or newer). Expect conflicts in `SaInvoiceServiceTests.cs` and `SaDoServiceTests.cs`. Keep origin’s WAC/FIFO replay tests. Re-run those tests **and** the 284-test pricing filter after rebase.
2. **Isolated SQL Server** scratch (`SqlServerTestConnection` name contains `test`, `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`) for P01–P08 and lock/session cases. Sqlite is not that proof.
3. **Browser UAT** of: DO→Invoice picker (discounts/basis, no extra click), qty change on a linked line (price frozen, amounts move), Price Info recorded vs current-rule, CN copy warning when amount2 is present, Price Inquiry labelled as current-rule.
4. Keep **A6 BLOCKED** and **C18 unproven** in any merge notes. Do not describe linked-line authorization as complete.
5. Keep **C13 / C15** in release notes as known limitations.

---

## 8. GO / NO-GO

| Question | Answer |
|---|---|
| Approve Phase A commercial code as written (A1–A5, A7)? | **Yes, as local uncommitted work on `e24f540`**, subject to rebase. |
| Approve merge to current `productionv2`? | **NO-GO** until fix list item 1 (and then 2–3). |
| Approve 10/10 / production-ready? | **NO-GO.** |
| Approve A6 or Phase B? | **NO-GO.** Not in this review. |
| Re-run G1–G5 for a code FAIL? | **No.** No blocking commercial defect found. Re-verify after rebase and SQL Server. |
