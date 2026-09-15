---
name: Sales Master Standard Lookups
overview: Replace every free-text reference input on the Sales master edit forms (item, item class, UOM, customer, sales rep, pay code, currency, area, state, country) with the lookups that already exist — IIvInventoryLookupService for inventory masters, ISaCustLookupService for sales reference codes and (new, ungated) customers and sales reps. Add the four missing server-side validators so a widget is never the only boundary, keep retired values visible instead of blanking them, and scope each picker to the width of the target column so it cannot offer a value the service will reject on save.
todos:
  - id: lookups-contract
    content: ISaCustLookupService + SaCustLookupService gain ListSalesRepsForAssignmentAsync / ValidateSalesmanCodeAssignmentAsync and ListCustomersForAssignmentAsync / ValidateCustCodeAssignmentAsync, both fail-closed via the existing ValidateLegacyOrFailClosed helper, and both append the caller's current persisted value when it is retired or inactive (orphan clause)
  - id: lookup-contract-doc
    content: Adopt the normative lookup contract (section 3) — active/orphan rule, six-clause company isolation, filtering-vs-validation separation, loading/failure behaviour, width-filter UX, shared code-length policy, description derivation
  - id: popup-width
    content: IvStockMasterSearchRequest, IvStockMasterSearchPopup AND IvStockMasterPicker gain an optional MaxICodeLength, applied in IvStockMasterRepository.SearchActiveAsync — the screens use the picker, which wraps the popup, and the popup loads a single 200-row page with no server paging
  - id: combo-width
    content: IvCodeComboBox gains an additive MaxCodeLength (null default) routed through LookupCodePolicy.Fits, and every converted combo declares its destination width from the EF configuration — five of the six new combos have a source/target mismatch
  - id: shipped-width
    content: Eight pre-existing SaCust width mismatches (CustType, CustGroupCode, AreaCode, Industry/Channel, PayCode, TaxGrCode, Currency, GroupDiscount) plus PoSupplier.AreaCode are fixed or scheduled — today they surface as SQL truncation errors, not validation errors
  - id: item-cust
    content: SaItemCustList — Customer, Item, Selling UOM, Currency converted from DxTextBox to IvCodeComboBox / IvStockMasterPicker; picker auto-fills IDesc
  - id: dis-group-item
    content: SaDisGroupItemList — Item to IvStockMasterPicker (auto-fill IDesc), Item class to IvCodeComboBox over ListActiveClassesAsync (nullable = All classes)
  - id: cust-price-group
    content: SaCustPriceGroupList line editor — Item to picker (fill IvCustPriceLineVm.IDesc), UOM to combo; OnLineAddOrUpdate key logic unchanged
  - id: cust-entry
    content: SaCustEntry — Salesman to IvCodeComboBox over ListSalesRepsForAssignmentAsync with the orphan clause; GL code stays free text; CreditTerm stays a documented stub
  - id: dis-group
    content: SaDisGroupList — Pay Code combo, member Customer code combo (drop the SA_CUST_PROFILE-gated ISaCustService.GetAsync dependency), GroupStatus + DiscountType option lists
  - id: lmw
    content: SaLMWList — Customer Code combo, auto-fill CustName
  - id: filters-salesrep
    content: SaCustList filter popup Salesman + Area combos; SaSalesRepList State + Country combos
  - id: orphan-helper
    content: One append helper with a fixed signature (offer list + current code + optional description) plus one LookupCodePolicy.Fits(code, maxLength); IvCodeComboBox owns the filter-append-hint order so no page can reorder it
  - id: server-idesc
    content: SaItemCust.IDesc and IvCustPrice.IDesc are derived server-side from IvStockMaster, and SaLMW.CustName from SaCust, instead of being trusted from the client model (ItemFamily.cs 393/407/853/892, Lmw.cs 240)
  - id: header-vocab
    content: SaDisGroup.GroupStatus becomes derived/read-only (or a dropdown over the live vocabulary) and both it and SaDisGroup.DiscountType get the missing server-side membership check — the header save currently only truncates them
  - id: validators
    content: Server-side validation for SaCust.SalesmanCode, SaDisGroup.PayCode, SaLMW.CustCode, SaSalesRep.State/Country using the six-clause contract in section 3.2
  - id: phases
    content: Re-sequence so each functional area ships UI + server validation + tests in the same phase; the cross-cutting regression and security suite is Phase 4
  - id: tests
    content: Test matrix A-L (section 7) in the named existing suites, plus positive / negative / orphan / failure manual smoke on every converted screen
  - id: register
    content: Section 11 decision register signed by the owner before Phase 0 — this is the production-approval gate
isProject: false
---

# Plan: Sales Master Standard Lookups

> **Status: NOT APPROVED FOR PRODUCTION YET.** Three review passes are folded in below (section 0).
> Phase 0 does not start until the section 11 decision register is signed — that register *is* the
> production-approval gate. The architecture is settled; the remaining work is contract and
> test closure, not redesign.

## 0. Review record

Four review passes were applied to this plan. All are folded in; where they conflict the stricter rule wins. The most recent pass is listed first.

| Review | Score | Verdict | Disposition |
|---|---|---|---|
| Author review (evidence audit of every reference in this file, against the code) | 7.5/10 | workable, not decision-closed | 2 blocking gaps, 3 silent-bug risks, 6 accuracy defects — all folded in |
| Reviewer pass (lookup-contract hardening) | 9.2/10 | strong, close to implementation-ready | 6 required improvements, 6 new tests, phase re-sequencing — all folded in |
| Owner review (third pass) | — | 2 blockers | W1 combo width filtering, W2 cross-company legacy value — both folded in |
| Owner review (fourth pass) | — | 4 issues | O1 ordering, O2 lookup API contract, O3 name derivation, O4 dropdown validation — all folded in |

Combined readiness once section 11 is signed: **9.6/10** — the residual risk is entirely the three open owner decisions in register rows 1, 15 and 20.

### Fourth pass — four issues found by the owner review (now closed)

| # | Issue | Why it was real | Where it is now answered |
|---|---|---|---|
| O1 | An over-width persisted orphan could be appended and then filtered out again | `IvCodeComboBox` filtering `Data` internally would remove the very value the orphan rule re-appended — the S1 hazard returning through a second route | Section 3.1.1 — fixed ordering, owned by the control |
| O2 | `ListSalesRepsForAssignmentAsync` / `ListCustomersForAssignmentAsync` had no explicit way to receive the current persisted code | The plan required "append the caller's current persisted value" but the signature is `(CancellationToken)` only, so the rule was not implementable from the contract | Section 3.1.1 — the contract moves to a fixed-signature helper, with the reasoning for keeping the service shape uniform |
| O3 | `SaLMW.CustName` was only auto-filled by the UI | `SaLMWEditVm.CustName` is client-supplied (`ISaSalesRefService.cs:510`) and written through (`SaSalesRefService.Lmw.cs:240`); `:100` checks length only — so the name is client-trusted, exactly like `IDesc` | Section 3.7 — server derives it from `SaCust`; Phase 2 step 16 |
| O4 | `GroupStatus` / `DiscountType` becoming dropdowns with no server-side validation | Header save only truncates them (`SaSalesRefService.cs:2244-2245`, `:2284-2285`) — no vocabulary check at all, unlike the item-level validator (`DisGroupItem.cs:163/168/177`) | Section 3.9; Phase 2 steps 14 and 17 |

### Wording and register inconsistencies corrected in this pass

| Was | Now |
|---|---|
| "Two review passes are folded in" | Four passes; the count and the readiness figure now match the register |
| Readiness quoted as 9.7/10 while two rows were `BLOCKED` | 9.6/10, with the three open decisions named (rows 1, 15, 20) |
| Register row 5 covered `IDesc` only | Extended to descriptions **and** names (O3) |
| Test G covered `IDesc` only | Extended to `SaLMW.CustName`, plus new tests M and N |
| `server-idesc` todo named two fields | Names all three derived fields |

### Third pass — two blockers found by the owner review (now closed)

| # | Blocker | Why it was real | Where it is now answered |
|---|---|---|---|
| W1 | Width filtering was designed for `IvStockMasterPicker` only; the `IvCodeComboBox` fields carry the same source/target mismatches (customer, UOM, currency, item class, pay code) | The plan gave the item side a mechanism (`MaxICodeLength` + a repository filter) and the code-combo side only a policy name, so five converted combos would keep offering over-width codes | Section 3.5.2 — `MaxCodeLength` on `IvCodeComboBox` + `LookupCodePolicy`; Phase 0 steps 3a–3c |
| W2 | Clause 5 tolerated an unchanged value belonging to **another company**, with no security decision | Silently tolerating a cross-company reference hides a tenant-integrity issue; rejecting it would revive the D-6 regression that clause 5 exists to prevent | Section 3.2.1 — the tolerance is a **snapshot string comparison that never reads another company's data** |

**W1 is larger than it first appeared.** Auditing every `SaCust` reference column against its source master found **eight mismatches already shipped** on `SaCustEntry` (section 3.5.3) — none introduced by this plan, all with the same failure mode: `SaCustService` neither truncates nor length-checks those fields (`:585-588` assigns directly, the validators at `:684-761` check existence only), so an over-width pick reaches a narrower column and surfaces as a SQL truncation error (HTTP 500) instead of a field validation error.

### Blocking gaps found by the author review (now closed)

| # | Gap | Where it is now answered |
|---|---|---|
| B1 | `MaxICodeLength` was named only on the popup; the screens use `IvStockMasterPicker`, and `IvStockMasterSearchRequest` had no such field | Section 3.5; Phase 0 step 3 |
| B2 | The width decision was left open, so Phase 1 could not start | Section 3.5; section 11 row 1 (signed before Phase 0) |

### Silent-bug risks (now closed)

| # | Risk | Where it is now answered |
|---|---|---|
| S1 | A retired persisted code renders blank in a combo — the exact D-6 clause-3 hazard, and the repo's existing mitigation (`CreditTerms`) covered only one field | Section 3.1; Phase 0 step 5 |
| S2 | `IDesc` is client-trusted (`SaSalesRefService.ItemFamily.cs:393/407/853/892`), so the picker auto-fill is a convenience, not a control | Section 3.7; Phase 1 step 10 |
| S3 | Enable-gating differs per screen (`IsEditMode` vs `EditEnabled`), so the wrong flag makes the price-group line editor writable in view mode | Phase 1 step 9; Phase 2 steps |

### Accuracy defects corrected

| # | Was | Now |
|---|---|---|
| A1 | `SaSalesRefService.Lmw.cs:100` cited for the `CustCode` length check | `:94` — `:100` is `CustName` |
| A2 | `SaCustEntry.razor` "~745 Credit term" | `:719-720` |
| A3 | `SaCountry.CountryCode` "50 / verify" | 20 — no overflow risk; the row now reads "ok" |
| A4 | "matching the PERCENTAGE/AMOUNT vocabulary" | `SaDiscountSlotTypes.Percentage/Amount`, already consumed at `SaDisGroupItemList.razor.cs:28-32` |
| A5 | sales-rep lookup `Desc = <name>` | `Desc = SrepName`; and `SaSalesRep` has **no active flag**, so the lookup must not filter on one |
| A6 | "alongside the existing sales master suites" | the suites are named in the test matrix (section 7) |

## 1. Objective

Every Sales master edit form that stores a **reference** must use the standard lookup widget instead of a free-text box. Today an operator types a code blind and learns it is wrong only on Save, because the service validates the value but the input never showed what was valid.

## 2. Scope

In scope — the editable Sales master surfaces under `ErpWeb.UI/Sales/Masters/`:

| Page | Route | Edit surface |
|---|---|---|
| `SaCustEntry.razor` | `/sales/customers/{mode}` | Full-page form (the only `*Entry` page) |
| `SaItemCustList.razor` | `/sales/customer-items` | Popup form |
| `SaDisGroupItemList.razor` | `/sales/item-discounts` | Popup form |
| `SaCustPriceGroupList.razor` | `/sales/price-groups` | Popup form + line sub-form |
| `SaDisGroupList.razor` | `/sales/discount-groups` | Popup form + members grid |
| `SaLMWList.razor` | `/sales/lmw` | Popup form |
| `SaSalesRepList.razor` | `/sales/sales-reps` | Popup form |
| `SaCustList.razor` | `/sales/customers` | Filter popup |

Out of scope — pure code+description masters (`SaCustTypeList`, `SaCustGroupList`, `SaCustSubGroupList`, `SaAreaList`, `SaCountryList`, `SaCurrencyList`, `SaCurrRateList`, `SaPaymentTermList`, `SaShipViaList`, `SaSOTypeList`, `SaCommentList`, `SaShippingLeadTimeList`), where the `Code` box is the master's own key. `SaCustPriceList.razor` is read-only. GL code / Tax GL code stay free text — no GL account master exists anywhere in Blazor (`MsDept.GlCode`, `IvStockMaster.SellingGlCode`/`PurchaseGlCode`, `PoSupplier.GlCode` and `SaTaxGroup.TaxGlCode` are all free text today).

## 3. Lookup contract (normative)

Every converted field obeys this section. The UI wiring is the easy part; this contract is what makes the change safe in production.

### 3.1 Active / orphan policy — one rule for every lookup

| Row state | What the list offers | What the control shows | What the server does |
|---|---|---|---|
| New record | active rows, caller's company only | the picked value | validates against active rows |
| Existing record, value still active | active rows | the value | validates against active rows |
| Existing record, value retired or inactive | active rows **plus the row's own persisted value** | the persisted value — never blank | tolerates the unchanged persisted value |
| Existing record, value absent from the master entirely (orphan) | active rows plus the persisted value | the persisted value, flagged | tolerates it — never blocks an unrelated edit |

Rules:

1. A control must **never** render blank because the persisted code is retired. The repo already has the pattern — `SaCustEntry.razor.cs:57` appends the current value to `CreditTerms` when it is missing from the list. That becomes **one shared helper** used by all six new combos and the picker.
2. The UI rule and the server rule must agree: a value the UI shows must be a value the server accepts when it is unchanged.
3. Blanking is a user action, never a side effect of an inactive lookup row.
4. The append is display-only. Integrity is enforced independently by clause 5 of the validator (section 3.2), so a page that fails to render the value cannot corrupt data — it can only inconvenience the operator.

### 3.1.1 One append, one order — ordering and the current-value contract

Two different filters can remove a value that is already on the row: the active-flag filter inside the assignment list, and the width filter in section 3.5. The append that restores it must therefore be **ordered and owned in exactly one place**, or it gets re-filtered and the value blanks anyway.

**Fixed order, evaluated by the control on every render:**

1. Offer list = the company-scoped active rows returned by the service.
2. Apply the width filter (`MaxCodeLength`, or `MaxICodeLength` on the item popup) to those rows.
3. Append the current `Value` if it is absent from the filtered list, synthesising `IvCodeLookupRow { Code = Value, Desc = orphanDescription ?? Value }`.
4. Compute the *"codes longer than N characters are excluded"* hint from step 2's excluded **active** rows only — the persisted value never counts toward it, and its presence must not suppress a hint that is genuinely needed for other rows.

**Ownership.** `IvCodeComboBox` performs steps 2–4 itself, so a page cannot get the order wrong and cannot blank a value by filtering. Pages additionally use the shared helper for display paths that are not a combo (member grids, read-only panes). The item picker is naturally exempt: its text box renders `ICode` directly rather than from a list, so only the popup's search rows are width-filtered.

**API contract for the current value.** The list methods keep the repository's existing shape — `ListXxxForAssignmentAsync(CancellationToken)` — and do **not** gain an `includeCode` parameter. Three reasons:

1. All 13 shipped assignment lists have that shape; a lone variant invites the next implementer to copy whichever they see first.
2. The page already holds the row, so it can supply a real description (`SaItemCust.IDesc`, `SaLMW.CustName`, `SaDisGroupMemberVm.CustName`) where the service could only return a bare code.
3. The append is display-only — clause 5 of the validator tolerates the unchanged value independently — so the contract is not load-bearing for integrity.

The contract therefore belongs on the helper, with a fixed signature: *offer list + current code (+ optional description) → the list with the current code present*.

**Consequence, stated so it is not a surprise.** When no description is available, an orphan renders as the bare code — flagged, never blank. That is deliberate: resolving a name would mean reading a master the caller may not be entitled to see (section 3.2.1 clause 3).

### 3.2 Company isolation — six clauses

Every new `ValidateXAssignmentAsync` implements exactly this, modelled on the shipped `ValidateLegacyOrFailClosed` helper:

| # | Input | Result |
|---|---|---|
| 1 | blank, field is optional | valid |
| 2 | non-blank, exists in the caller's company | valid |
| 3 | non-blank, exists only in another company | **reject** |
| 4 | non-blank, exists nowhere | **reject** |
| 5a | non-blank, equals the row's current value, and resolves to no visible row | **tolerate** — legacy free text |
| 5b | non-blank, equals the row's current value, and resolves only to another company | **tolerate the save; never read the value** — section 3.2.1 |
| 6 | list is empty because the caller has no company scope | fail closed for masters; legacy-adjacent fields keep the documented empty-list bypass |

The cross-company rule is never implicit. The list methods filter on `scope.CompanyCode`.

### 3.2.1 Cross-company legacy value — the security decision

Clause 5 is the migration-safety clause inherited from D-6: without it, every record carrying a retired or free-typed code becomes uneditable. The owner review correctly asked what it means when the unchanged value happens to belong to **another company**. The resolution is a mechanism, not a judgement call:

1. **Clause 5 is a string comparison, not a lookup.** It compares the incoming value against the row's own snapshot by ordinal-ignore-case equality and returns valid **before** any list or lookup call. The shipped `ValidateLegacyOrFailClosed` already behaves exactly this way — the equality test precedes `listAsync` — so the tolerance can never read another company's master data.
2. **The validators never query across companies.** They call the company-scoped list and compare inside its result. There is no "search all companies for this code" path, so there is nothing to leak.
3. **Cross-company rows are never offered or displayed.** The pickers and list methods stay company-filtered, and the orphan rule (section 3.1) shows the **raw persisted code** — it never resolves a display name from a master the caller cannot see.
4. **Clause 5 cannot change a value.** Any new or changed value must pass clauses 2–4. Clause 5 only ever applies to the value already on the row.
5. **An unchanged value is not re-written.** `SaCustService` uses `SetIfChanged`, so a tolerated value is not propagated to the update — tolerance never preserves or spreads a cross-company reference.

**Residual risk, handled explicitly.** Clause 5 does not *detect* a genuine cross-company stored value; it only declines to block an unrelated edit on a legacy row. Detection is a **data-quality** task, not a permission task, and it is a separate artefact: a **count-only** diagnostic for the Phase 0 findings (no names, no cross-company reads), followed by an owner decision on remediation. Do **not** make the validator reject an unchanged cross-company value — that reintroduces exactly the regression D-6 was written to prevent.

### 3.3 Filtering is not validation

```
lookup filtering    -> controls what the operator can select   (UX)
server validation   -> controls what the system accepts        (integrity, API, imports, jobs)
```

A 20-character item picker does **not** remove the obligation for `SaveAsync` to prove the item exists in the caller's company. Both are required, and a direct service call must be rejected even when the UI would never have offered the value.

### 3.4 Loading and failure behaviour

| State | Control behaviour |
|---|---|
| loading | disabled, or `IsLoading` — never an empty-looking list |
| loaded, zero active rows | "No records found" |
| lookup call failed | a **visible error**; never silently treated as an empty lookup |
| row carries a retired value | the value stays visible (section 3.1) |

`IvCodeComboBox` already exposes `IsLoading`. `IvStockMasterPicker` and its parent pages need the same treatment — a failed `SearchStockMastersAsync` must not look like "no items exist".

### 3.5 Width filtering — both control types

A control that offers a code the destination column cannot store is a dead end: the operator selects a valid row and the save fails. That applies to **every** control, not only the item picker.

#### 3.5.1 Destination widths this plan touches

| Control | Target column | Width | Source | Width |
|---|---|---|---|---|
| item picker | `SaItemCust.ICode` / `IvCustPrice.ICode` / `SaDisGroupItem.ICode` | 20 | `IvStockMaster.ICode` | **30** |
| code combo | `SaItemCust.CustCode` | 20 | `SaCust.CustCode` | **30** |
| code combo | `SaItemCust.SellingUOM` / `IvCustPrice.UOM` | 5 | `MsUom.UOMCode` | **10** |
| code combo | `SaItemCust.Currency` | 5 | `SaCurrency.CurrCode` | **20** |
| code combo | `SaDisGroupItem.IClass` | 10 | `IvClass.IClassCode` | **30** |
| code combo | `SaDisGroup.PayCode` | 40 | `IvMsCode.Code` | **50** |
| code combo | `SaLMW.CustCode` | 30 | `SaCust.CustCode` | 30 — ok |
| code combo | `SaCust.SalesmanCode` | 20 | `SaSalesRep.SrepCode` | 20 — ok |

#### 3.5.2 Mechanism — one parameter per control type, one shared policy

- **Item side:** `MaxICodeLength` on **both** `IvStockMasterSearchPopup` **and** `IvStockMasterPicker` (the screens use the picker, which wraps the popup), applied as a **new field on `IvStockMasterSearchRequest`** so the filter runs inside `IvStockMasterRepository.SearchActiveAsync`. Post-filtering the loaded page is not acceptable — the popup loads a single 200-row page with no server paging.
- **Code side:** **`MaxCodeLength` on `IvCodeComboBox`**, filtered against `Data` inside the component, defaulting to `null` = no filtering. `IvCodeComboBox` is used in 18 files across Inventory, Purchase and Sales, so the parameter must be additive with a null default — no existing call site changes behaviour.
- **One policy:** `LookupCodePolicy.Fits(code, maxLength)` (section 3.6) is the only place the comparison is written. Every converted field declares its destination width from the EF configuration, not from a literal typed into the Razor.

#### 3.5.3 Pre-existing mismatches this plan must not ship next to

Auditing every `SaCust` reference column against its source master found **eight mismatches already shipped**. None are introduced here, and all share one failure mode: `SaCustService` neither truncates nor length-checks these fields (`:585-588` assigns directly; the validators at `:684-761` check existence only), so an over-width selection reaches a narrower column and surfaces as a SQL truncation error (HTTP 500) rather than a field validation error.

| `SaCust` column | Width | Source master | Width |
|---|---|---|---|
| `CustType` | 20 | `SaCustType.CustTypeCode` | **40** |
| `CustGroupCode` | 20 | `SaCustGroup.CustGroupCode` | **40** |
| `AreaCode` | 20 | `IvAreaCode.AreaCode` | **40** |
| `IndustryCode` / `ChannelCode` | 20 | `IvMsCode.Code` | **50** |
| `PayCode` | 20 | `IvMsCode.Code` | **50** |
| `TaxGrCode` | 20 | `IvMsCode.Code` | **50** |
| `Currency` | 10 | `SaCurrency.CurrCode` | **20** |
| `GroupDiscount` | 20 | `SaDisGroup.GroupName` | **40** |

`PoSupplier.AreaCode` (20) against `IvAreaCode.AreaCode` (40) is the same defect outside Sales. Register row 15 decides the scope; the minimum is that the **Sales** set ships with this plan, because its whole premise is that the operator cannot pick a value the service will refuse.

#### 3.5.4 Rules

1. An offer list excludes codes that cannot fit; the **currently persisted value is never excluded** (section 3.1.1) even when it is over-width — it is already stored and must stay editable. The order is fixed and owned by the control: filter the active rows → append the persisted value if absent → compute the exclusion hint from the filtered **active** rows only.
2. Exclusions are explained, never silently hidden: *"No matching items can be stored in this field — codes longer than 20 characters are excluded."* A bare "No records found" makes the operator believe the row does not exist.
3. Server-side length validation is added for every converted field, so a direct service call cannot reach the database and raise a truncation error (the pre-existing gap in 3.5.3). The client filter is UX; the service check is integrity (section 3.3).
4. Before any screen work, run the `MAX(LEN(...))` pre-check per source master. If live data already exceeds the destination width, **stop** and close section 11 row 1 — do not silently hide existing data.

### 3.6 Shared code-length policy

`LookupCodePolicy.Fits(code, maxLength)` (or one equivalent helper) is the **only** place the length comparison is written. Every converted field declares its destination width from the EF configuration, not from a literal typed into the Razor.

### 3.7 Description and name integrity

Three descriptive fields are written straight from the client model today, which makes every UI auto-fill a convenience rather than a control:

| Field | Written at | Should be derived from |
|---|---|---|
| `IvCustPrice.IDesc` | `SaSalesRefService.ItemFamily.cs:393/407` | `IvStockMaster.IDesc` for the resolved `ICode` |
| `SaItemCust.IDesc` | `SaSalesRefService.ItemFamily.cs:853/892` | `IvStockMaster.IDesc` for the resolved `ICode` |
| `SaLMW.CustName` | `SaSalesRefService.Lmw.cs:240` (`:100` checks length only) | `SaCust.CustName` for the resolved `CustCode` |

The **service** must derive each of these for the resolved key, and the DTO field becomes an output rather than an input. Otherwise a crafted request stores an arbitrary description or customer name against a real key. Where the resolved master row itself has no description, store null rather than the client's text.

The item picker's auto-fill and the LMW customer auto-fill stay — as UX only.

### 3.8 Controlled vs derived — who owns each converted value

Turning a free-text box into a dropdown is a statement about ownership. Decide it before adding a control:

| Kind | Meaning | In scope |
|---|---|---|
| **Derived** | the service writes it; the DTO field is output-only; the form shows it read-only | `IvCustPrice.IDesc`, `SaItemCust.IDesc`, `SaLMW.CustName` (section 3.7); `GroupStatus` |
| **Controlled** | the operator picks from a fixed vocabulary and the service validates the value | `SaDisGroup.DiscountType`, `SaItemCust.SellingUOM`, `SaDisGroupItem.IClass`, every assignment code |
| **Free text** | genuinely unconstrained | addresses, contact names, remarks, GL code |

`GroupStatus` is the trap. It is the **activation flag** consumers filter on, yet today it is an editable `DxTextBox` whose value the header save merely truncates (`SaSalesRefService.cs:2244-2245`, `:2284-2285`). Making it a dropdown would let an operator pick a status that silently breaks the consumer filter.

Recommended treatment: **make it derived** — read-only in the form, written with the activation value by the service, and changed only through the page's activate/deactivate action rather than a text box. If the owner needs operator-chosen statuses, take the alternative: a dropdown over a named vocabulary **plus** the validator in section 3.9.

### 3.9 Vocabulary validation for every controlled field

A dropdown is a widget, not a boundary (section 3.3). Every field converted to a controlled input gets a server-side membership check against the *same* vocabulary the control offers:

| Field | Vocabulary source | Server check today |
|---|---|---|
| `SaDisGroupItem.DiscountType` / `DiscountType1` | `SaDiscountSlotTypes` | **exists** — `DisGroupItem.cs:163/168` |
| `SaDisGroupItem.EffectPrice` | `SaEffectPriceOptions` | **exists** — `DisGroupItem.cs:177` |
| `SaDisGroup.DiscountType` | `SaDiscountSlotTypes`, **only if** the header vocabulary is proven identical (step 17) | **missing** — add |
| `SaDisGroup.GroupStatus` | the activation vocabulary, or derived (section 3.8) | **missing** — add, or stop accepting it |
| `SaSalesRep.State` / `Country`, `SaCust.State` / `Country` | `IvMsCode` / `SaCountry` | exists via the assignment validators |

Two rules:

1. The validator accepts exactly the offered vocabulary **plus the unchanged persisted value** under clause 5 — so existing off-vocabulary rows stay editable.
2. Where a vocabulary is not yet proven, **do not invent one and do not add a dropdown**; leave the control as free text and record why (step 17).

## 4. Basis — verified findings

### Free-text reference inputs to convert

| Page | Line | Field | Today |
|---|---|---|---|
| `SaItemCustList.razor` | 64 | Customer | `DxTextBox` |
| `SaItemCustList.razor` | 72 | Item | `DxTextBox` |
| `SaItemCustList.razor` | 87 | Selling UOM | `DxTextBox` maxlength 5 |
| `SaItemCustList.razor` | 123 | Currency | `DxTextBox` maxlength 5 |
| `SaDisGroupItemList.razor` | 64 | Item | `DxTextBox` |
| `SaDisGroupItemList.razor` | 72 | Item class | `DxTextBox` ("All classes") |
| `SaCustPriceGroupList.razor` | ~95 | Line Item | `DxTextBox` |
| `SaCustPriceGroupList.razor` | ~99 | Line UOM | `DxTextBox` |
| `SaCustEntry.razor` | 309 | Salesman | `DxTextBox` |
| `SaDisGroupList.razor` | 72 | Pay Code | `DxTextBox` |
| `SaDisGroupList.razor` | ~129 | Member Customer code | `DxTextBox` |
| `SaDisGroupList.razor` | 85 / 92 | Group Status / Discount Type | `DxTextBox` |
| `SaLMWList.razor` | 73 | Customer Code | `DxTextBox` (`CustName` hand-typed) |
| `SaSalesRepList.razor` | 104 / 114 | State / Country | `DxTextBox` |
| `SaCustList.razor` | 158 / 165 | Salesman / Area filters | `DxTextBox` |
| `SaCustEntry.razor` | 719-720 | Credit term | `IvCodeComboBox` over a one-option stub |

### Lookups that already exist and must be reused

- **`IIvInventoryLookupService`** (`ErpWeb.Core/Inventory/IvInventoryLookupService.cs`) — **ungated**: `SearchStockMastersAsync`, `ListActiveTypesAsync`, `ListActiveClassesAsync`, `ListActiveSubClassesAsync`, `ListActiveUomsAsync`, `ListActiveWarehousesAsync`, `ListActiveLocationsAsync`, `ListClassificationsAsync`. Returns `IvInventoryLookupResult` (`OkItems` / `OkRows`).
- **Components** — `IvStockMasterPicker` (`ICode`, `ICodeChanged`, `Selected`, `Enabled`, `InputCssClass`), `IvStockMasterSearchPopup`, `IvCodeComboBox` (`Data`, `Value`, `ValueChanged`, `Enabled`, `ReadOnly`, `IsLoading`, `InputCssClass`, `NullText`).
- **`ISaCustLookupService`** — `List*ForAssignmentAsync` + `Validate*AssignmentAsync` pairs for Types, Groups, SubGroups, Areas, Countries, Currencies, DisGroups, States, TaxGroups, PayCodes, PriceGroups, Industries, Channels. It has **no customer list** and **no sales-rep list**.
- **Customer list** — `IMsRefService.ListActiveLookupsAsync()` returns `MsRefLookupBundle { Departments, Projects, Customers }`; reused from Admin master UI in `ErpWeb.UI/Admin/Master/MsProjectList.razor:78-89`.
- **Sales-rep master** — `SaSalesRep` / `db.SaSalesReps`. `ISaSalesRefService.ListSalesRepsAsync` exists but is gated by `MenuCodes.SalesSalesRep` (`SaSalesRefService.cs:1494`).
- **Display row** — `IvCodeLookupRow { Code, Desc, Rate, DisplayText }`.

### Server-side validation already present

- `SaSalesRefService.ItemFamily.cs:25-32` — `ItemCodeMax = 20`, `UomMax = 5`, `ClassMax = 10`, `CustCodeMax = 20`, `PriceGroupCodeMax = 20`; validators at `:289` (item exists), `:294` (UOM exists), `:812/:818/:824` (`SaItemCust` customer / item / UOM).
- `SaSalesRefService.ItemFamily.DisGroupItem.cs:187` (item), `:196` (class), `:163/:168` (discount type vocabulary), `:177` (effect price vocabulary).
- `SaCustService.cs:684-761` — the D-6 validation block for cust type, group, sub-group, area, country, ship country, ship state, industry, channel, currency, discount group, price group, state, tax group, pay code.

### Server-side validation missing

- `SaCust.SalesmanCode` — never validated.
- `SaDisGroup.PayCode` — length only (`SaSalesRefService.cs:2144`).
- `SaLMW.CustCode` — length only (`SaSalesRefService.Lmw.cs:94` — note `:100` is `CustName`); customer resolved manually in `SaDisGroupList.razor.cs:196`, not in LMW.
- `SaSalesRep.State` / `SaSalesRep.Country` — length only.

### Width mismatches (target column vs source code)

The matrix and the rule live in section 3.5. The fact that matters here: the **service already enforces the destination caps** — `SaSalesRefService.ItemFamily.cs:25-32` defines `ItemCodeMax = 20`, `UomMax = 5`, `ClassMax = 10`, `CustCodeMax = 20` — so an unscoped picker offers values the save will refuse.

### Latent bug found

`SaDisGroupList.razor.cs:196` resolves a member with `ISaCustService.GetAsync`, which requires `MenuCodes.SalesCustomerProfile` + `Access` (`SaCustService.cs:60-70`). A user holding `SA_DIS_GROUP` but not `SA_CUST_PROFILE` cannot add members today. The replacement lookup must be ungated.

## 5. Steps

Each phase ships **UI + server validation + tests together**, so no phase leaves a screen depending on a rule that does not exist yet. The cross-cutting suite is Phase 4.

### Phase 0 — shared plumbing and contract (blocks everything)

1. **`ErpWeb.Core/Sales/ISaCustLookupService.cs` + `SaCustLookupService.cs`** — add four members, all implemented with the existing private `ValidateLegacyOrFailClosed` helper and the six-clause contract in section 3.2:
   - `ListSalesRepsForAssignmentAsync` — company-scoped `SaSalesReps`, ordered by `SrepCode`, projecting `IvCodeLookupRow { Code = SrepCode, Desc = SrepName }`. **`SaSalesRep` has no active flag**, so the list must not filter on one; it appends the caller's current persisted value when that value is not in the list (section 3.1).
   - `ValidateSalesmanCodeAssignmentAsync(string? code, string? existingCode, CancellationToken)` — `allowLegacyEmptyBypass: false`.
   - `ListCustomersForAssignmentAsync` — company-scoped `SaCusts`, `Code = CustCode`, `Desc = CustName`, same orphan clause.
   - `ValidateCustCodeAssignmentAsync(string? code, string? existingCode, CancellationToken)` — `allowLegacyEmptyBypass: false`.
2. **Do not** call `ISaSalesRefService.ListSalesRepsAsync` from `SaCustEntry` or `SaCustList` — the `SA_SALES_REP` menu gate would leak a permission. The sales-side home for assignment lists is `ISaCustLookupService`; `IIvInventoryLookupService` is already ungated for the item side.
3. **Width-filter plumbing — item side** (section 3.5.2) — add `MaxICodeLength` to `IvStockMasterSearchRequest`, `IvStockMasterSearchPopup` **and** `IvStockMasterPicker`, and apply it in `IvStockMasterRepository.SearchActiveAsync`. `IvStockMasterSearchPopup.LoadAsync` issues a bare `new IvStockMasterSearchRequest()` — one 200-row page with a client-side grid search — so a post-load filter would silently shrink an already-capped list.
3a. **Width-filter plumbing — code side** (section 3.5.2) — add `MaxCodeLength` to `IvCodeComboBox`, additive with a `null` default so the 18 existing files that use it are unaffected, and route the comparison through `LookupCodePolicy.Fits`. Each call site declares its destination width from the EF configuration.
3b. **Server-side length validation** (section 3.5.4 rule 3) — add length checks for every converted field on the write path, so an over-width value arriving from a direct service call is a field validation error rather than a SQL truncation exception.
3c. **Pre-existing mismatch remediation** (section 3.5.3) — apply the same two pieces to the eight already-shipped `SaCust` mismatches, and to `PoSupplier.AreaCode` if register row 15 widens the scope. The Sales set is in scope for this release.
4. **Width pre-check** — run `SELECT MAX(LEN(...))` per source master (`IvStockMaster.ICode`, `MsUom.UOMCode`, `SaCurrency.CurrCode`, `IvClass.IClassCode`, `IvMsCode.Code`, `SaCust.CustCode`, `SaCustType.CustTypeCode`, `SaCustGroup.CustGroupCode`, `IvAreaCode.AreaCode`, `SaDisGroup.GroupName`) and record the results **in this file**. If any live value exceeds its destination column, stop and close section 11 row 1 before touching a screen.
5. **Shared helpers** (section 3.1.1) — one append helper with the fixed signature *offer list + current code (+ optional description) → list with the current code present*, replacing the ad-hoc `CreditTerms` pattern (`SaCustEntry.razor.cs:57`); and one `LookupCodePolicy.Fits(code, maxLength)`. `IvCodeComboBox` owns the filter → append → hint order itself, so no page can reorder it.
6. **Reuse the shipped vocabularies** — `SaDiscountSlotTypes` and `SaEffectPriceOptions` already exist and are already consumed at `SaDisGroupItemList.razor.cs:28-32`. Do not declare new string literals.

### Phase 1 — item family (UI + validation + tests)

7. **`SaItemCustList.razor` / `.razor.cs`** — Customer → `IvCodeComboBox` over `ListCustomersForAssignmentAsync`; Item → `IvStockMasterPicker` with `Selected` auto-filling `IDesc`; Selling UOM → `IvCodeComboBox` over `ListActiveUomsAsync`; Currency → `IvCodeComboBox` over `ListCurrenciesForAssignmentAsync`. Load the lookups in `OnPageInitializedAsync` beside the existing `CanViewPrice` check, with the loading/failure behaviour from section 3.4. Keep `ReadOnly="@IsEditMode"` on the four key fields — they are part of the row identity.
8. **`SaDisGroupItemList.razor` / `.razor.cs`** — Item → `IvStockMasterPicker` (auto-fill `IDesc`); Item class → `IvCodeComboBox` over `ListActiveClassesAsync`, nullable (`NullText="All classes"`; the combo already renders a clear button).
9. **`SaCustPriceGroupList.razor` / `.razor.cs`** — line editor: Item → picker (auto-fill `IvCustPriceLineVm.IDesc`), UOM → combo. Leave the `OnLineAddOrUpdate` key construction unchanged apart from the casing/trim already in place. **Gate the new widgets on `EditEnabled`, not `IsEditMode`** — this is a view/edit region inside one popup, and the wrong flag makes the line editor writable in view mode.
10. **Server-side description derivation** — derive `IDesc` from `IvStockMaster` for the resolved `ICode` in `SaSalesRefService.ItemFamily.cs` (the price-line path at `:393/407` and the customer-item path at `:853/892`) instead of trusting `model.IDesc` (section 3.7). Item existence, UOM existence and the class check are already covered (`:289`, `:294`, `:818`, `:824`, `DisGroupItem.cs:196`).
11. **Phase 1 tests** — item / UOM / class rejection, cross-company rejection, retired-value tolerance, and description derivation (tests A–E and G in section 7) applied to these three screens.

### Phase 2 — customer, sales rep, discount group, LMW (UI + validation + tests)

12. **`SaCustEntry.razor` / `.razor.cs`** — Salesman (`:309`) → `IvCodeComboBox` over `ListSalesRepsForAssignmentAsync` with the orphan clause; route `SalesmanCode` into the existing `ValidationErrors` display path. Leave GL code free text.
13. **`SaCustService.SaveAsync`** — call `ValidateSalesmanCodeAssignmentAsync` with the existing snapshot, error key `SalesmanCode`, beside the validator block at `:684-761`.
14. **`SaDisGroupList.razor` / `.razor.cs`** — Pay Code (`:72`) → `IvCodeComboBox` over `ListPayCodesForAssignmentAsync`; member "Customer code" (`~:129`) → customer combo, keeping the duplicate check and the name resolution but **dropping the `ISaCustService.GetAsync` dependency**, which is gated by `SA_CUST_PROFILE` and silently blocks `SA_DIS_GROUP`-only users today (`SaCustService.cs:60-70`); `GroupStatus` (`:85`) → **derived / read-only** (section 3.8) rather than an operator-chosen value; `DiscountType` (`:92`) → `DxComboBox` over `SaDiscountSlotTypes` with a clear button (the column is nullable, so do not force a value).
15. **`SaDisGroup` save** in `SaSalesRefService.cs` — validate `PayCode` (today only a length check at `:2144`) by querying `IvMsCodes` inside the same db context. Do **not** inject `ISaCustLookupService`: the constructor is `(IDbContextFactory<AppDbContext>, IInventoryTenantContext, IAccessRightService, ICurrentDateService)` (`:23-31`) and changing it ripples into ~14 test files.
16. **`SaLMWList.razor` / `.razor.cs`** — Customer Code (`:73`) → customer combo; keep the `Key(LicenseNo, rowVersion, parentCode: CustCode)` shape intact; add customer existence validation on save (today `SaSalesRefService.Lmw.cs:94` checks length only). **`CustName` becomes derived** (section 3.7): the form shows it read-only, and `SaSalesRefService.Lmw.cs:240` writes `SaCust.CustName` for the resolved `CustCode` instead of `model.CustName`.
17. **Vocabulary closure and validation** (sections 3.8, 3.9) — read the exact `SaDisGroup.GroupStatus` values from live data; do not invent values. Decide `GroupStatus` as **derived** (recommended — read-only, activation value written by the service) or as a dropdown over that vocabulary. If the header `DiscountType` cannot be shown to be identical to `SaDiscountSlotTypes`, keep it free text and record why. Whichever route row 20 takes, add the **missing server-side membership check** for `SaDisGroup.GroupStatus` and `SaDisGroup.DiscountType`, with clause 5 tolerance for existing off-vocabulary rows.
18. **Phase 2 tests** — permission independence (test D), pay-code rejection, salesman cross-company rejection, LMW customer validation, and the `IvMsCode` pay-code path.

### Phase 3 — filters and remaining pages (UI + validation + tests)

19. **`SaCustList.razor`** — filter popup Salesman (`:158`) and Area (`:165`) → combos; Type and Group on the same dialog are already `IvCodeComboBox`. Filters are read-only queries, so no new server validator is needed, but the filter values remain company-scoped by the list service.
20. **`SaSalesRepList.razor` / `.razor.cs`** — State (`:104`) → `ListStatesForAssignmentAsync`, Country (`:114`) → `ListCountriesForAssignmentAsync` (the same two `SaCustEntry` uses), **plus** the matching `ValidateXAssignmentAsync` calls on the `SaSalesRep` save, which validates length only today.
21. **`SaTaxGroupList.razor`** and `SaCustEntry` — `TaxGlCode` and `GlCode` stay free text; record the reason (no GL account master exists in Blazor).
22. **CreditTerm** — leave the single-option stub as-is and document it as a flag (section 11 row 6). Do not expand scope.

### Phase 4 — cross-cutting regression and security suite

23. Run the full section 7 matrix, including the tests that span screens — D (permission independence), E (direct `SaveAsync` bypass), F (lookup failure), I (list-level company isolation) — plus the SQL Server concurrency suite under `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`.
24. **Confirm no menu or permission change is required.** No menu code, no `menus.xml` entry, no `scripts/init-*-menu.sql`, no `MenuDeploymentParityTests` change. That is a feature of this plan; state it so a reviewer does not go looking for one.

## 6. Relevant files

- `ErpWeb.Core/Sales/ISaCustLookupService.cs`, `SaCustLookupService.cs` — new assignment lists and validators (step 1)
- `ErpWeb.UI/Inventory/Lookups/IvCodeComboBox.razor.cs`, `IvStockMasterPicker.razor.cs`, `IvStockMasterSearchPopup.razor.cs` — reused widgets plus `MaxICodeLength`
- `ErpWeb.Core/Inventory/IvInventoryLookupService.cs` — ungated item / class / UOM lists, reused as-is
- `ErpWeb.UI/Sales/Masters/SaItemCustList.razor`, `SaDisGroupItemList.razor`, `SaCustPriceGroupList.razor`, `SaCustEntry.razor`, `SaDisGroupList.razor`, `SaLMWList.razor`, `SaCustList.razor`, `SaSalesRepList.razor` (each with its `.razor.cs`)
- `ErpWeb.Core/Sales/SaCustService.cs` (`:684-761` validator block), `SaSalesRefService.cs`, `SaSalesRefService.ItemFamily.cs`, `SaSalesRefService.ItemFamily.DisGroupItem.cs`, `SaSalesRefService.Lmw.cs`
- `ErpWeb.Model/Configurations/Sales/SaItemCustConfiguration.cs`, `IvCustPriceConfiguration.cs`, `SaDisGroupItemConfiguration.cs`, `SaDisGroupConfiguration.cs`, `SaLMWConfiguration.cs`, `IvMsCodeConfiguration.cs` — the width matrix
- `ErpWeb.UI/Admin/Master/MsProjectList.razor.cs` (`:30-43` orphan-tolerance pattern)
- `ErpWeb.Tests/SaItemFamilyServiceTests.cs`, `SaCustServiceTests.cs`, `SaCustSqlServerConcurrencyTests.cs`, `SaSalesRefServiceTests.cs`, `SaSalesMasterServiceTests.cs`, `SaRefMasterServiceTests.cs`, `SaLmwSqlServerConcurrencyTests.cs` — where the section 7 matrix lands
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs` (`:357-376`) — the reference pattern for loading a lookup bundle and surfacing a failure instead of an empty list

## 7. Test matrix

Automated tests go in the existing suites: `SaItemFamilyServiceTests.cs` (price group, customer item, item discount), `SaCustServiceTests.cs` and `SaCustSqlServerConcurrencyTests.cs` (customer and salesman), `SaSalesRefServiceTests.cs`, `SaSalesMasterServiceTests.cs` and `SaRefMasterServiceTests.cs` (reference masters), `SaLmwSqlServerConcurrencyTests.cs` (LMW). `SaSalesRefService` is constructed directly in roughly 14 test files with a four-argument constructor — that is why step 15 queries `IvMsCodes` instead of injecting a lookup service.

| # | Test | Setup | Expected |
|---|---|---|---|
| A | Cross-company rejection | company A submits a code that exists only in company B | reject, error keyed to the converted field |
| B | Retired value unchanged | row carries a retired or inactive code and the field is not touched | save succeeds (section 3.2 clause 5) |
| C | Retired value replaced with rubbish | same row, value changed to a non-existent code | reject |
| D | Permission independence | user holds `SA_DIS_GROUP`, not `SA_CUST_PROFILE` | customer assignment lookup loads, selection works, save succeeds when valid |
| E | UI bypass | call `SaveAsync` directly with an invalid item / customer / salesman | server rejects — proving the widget is not the boundary |
| F | Lookup failure | lookup service throws or returns a failure | a visible error; no silent empty selection |
| G | Description and name derivation (section 3.7) | post a price line, a customer item and an LMW licence with forged `IDesc` / `CustName` for a real key | stored values equal the master's (`IvStockMaster.IDesc`, `SaCust.CustName`); no client-supplied text survives |
| H | Width scope | an item whose `ICode` exceeds the destination width | not offered by the picker; if submitted directly, rejected with the length message |
| I | List-level company isolation | two companies seeded with same-code masters | each company's assignment list returns only its own rows |
| J | Combo width filter + server length check | a master row whose code exceeds the destination width, selected through `IvCodeComboBox`; then the same value posted directly to `SaveAsync` | not offered in the combo (with the exclusion hint); the direct call rejected by a **field validation error**, never a SQL truncation exception |
| K | Cross-company value, changed | row's current value changed to another company's code | rejected — clause 5 covers unchanged values only |
| L | Cross-company value, unchanged (section 3.2.1) | row already carries a code that exists only in another company; field untouched | save succeeds, no cross-company read is performed, and no name from the other company is rendered |
| M | Header vocabulary rejection (section 3.9) | `SaDisGroup` save with an off-vocabulary `DiscountType`, and with a forged `GroupStatus` if row 20 keeps it editable | field validation error; an unchanged legacy off-vocabulary value still saves (clause 5) |
| N | Filter → append → hint order (section 3.1.1) | a row whose persisted code is over-width **and** absent from the active list | the code stays visible in the combo, the exclusion hint counts active rows only, and re-saving the row unchanged succeeds |

Manual smoke covers the states a unit test cannot reach, per screen: widget opens, filters, selects, value round-trips on reload; the picker fills the description on the three item screens; a retired value is visible and savable unchanged; a failed lookup shows an error; and the widget is disabled while loading.

## 8. Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` clean after each phase.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` green, including the SQL Server concurrency suite via `ConnectionStrings:SqlServerTestConnection` (the scratch database name must contain "test") **with** `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` — otherwise those tests self-skip and a green run means nothing executed.
3. Negative smoke — an item code longer than 20 characters, a UOM longer than 5, an item class longer than 10, a pay code longer than 40 and a currency longer than 5 are neither offered by the widget nor accepted by the service; direct-URL access without ADD / EDIT permission is refused.
4. Orphan smoke — open a row whose code is retired and confirm the value is **visible**, saves unchanged, and is not blanked.
5. Loading/failure smoke — make the lookup service fail and confirm the control shows an error rather than an empty list.
6. Identity guard — `SaItemCust` customer / item / UOM / MOQ remain read-only in edit mode and changing them is still refused with the "create a new row instead" message.
7. Regression — `SaDisGroupItemList` band and date-window overlap rules still behave as before; `SaCustPriceList` still lists and exports; the shipped `SaDisGroup` / `SaDisCust` screens still save and list.

## 9. Decisions

- **Reuse, do not reinvent.** Inventory masters via `IIvInventoryLookupService` (item, class, UOM); sales reference codes via `ISaCustLookupService`; customers via a new `ListCustomersForAssignmentAsync`; sales reps via a new ungated assignment list. No new generic lookup service, and no new lookup component.
- **Naming convention.** Every future assignment lookup follows `ListXxxForAssignmentAsync` / `ValidateXxxAssignmentAsync`, so Sales, Purchase and Inventory additions stay consistent.
- **A widget is not an integrity boundary.** Every converted field gets its matching server-side validator (section 3.2), or an explicit note that one already exists.
- **Active/orphan policy** (section 3.1) applies to all six new combos and the item picker.
- **Width policy** (section 3.5). Default: scope the picker to the target width and explain the exclusions; stop before implementation if the `MAX(LEN(...))` pre-check shows live data already exceeds the destination.
- **Gating.** No sales master screen may depend on a menu-gated service for a lookup (`ISaSalesRefService.ListSalesRepsAsync` needs `SA_SALES_REP`; `ISaCustService.GetAsync` needs `SA_CUST_PROFILE`).
- **GL code stays free text** in this change, alongside `MsDept`, `IvStockMaster` and `PoSupplier`.
- **CreditTerm stays as-is** unless the business confirms a real vocabulary. Do not expand scope.
- **Price-group line UOM is independent of the item's `SellingUom`** — legacy deliberately allows one item at several UOMs and prices, so item-limited validation would break a real feature.
- **Description-derived flags are scoped.** `SaDisGroup.DiscountType` becomes a dropdown only if the header vocabulary is proven identical to `SaDiscountSlotTypes`; `SaLMW.LicenseType` stays free text unless a vocabulary is confirmed.

## 10. Further considerations

1. **`SaCust.CreditTerm`** is bound to a single hard-coded option `"CREDIT LIMIT"` in `SaCustEntry.razor.cs:57`, with the stored value appended. Decision: leave as-is and document it as a flag; revisit only if the owner confirms a real vocabulary exists (section 11 row 6).
2. **`SaDisGroup.DiscountType` vs `SaDisGroupItem.DiscountType`** — the item validator enforces `PERCENTAGE`/`AMOUNT` via `SaDiscountSlotTypes`. One shared option list for both is the goal, but only after the header vocabulary is confirmed identical (section 11 row 8).
3. **`SaLMW.LicenseType`** — becomes a dropdown only if a code vocabulary is confirmed; otherwise it stays free text (section 11 row 11).
4. **`SaCustPriceGroupList` line UOM** — legacy deliberately allows the same item at several UOMs and prices, so the UOM picker must not be constrained to the item's `SellingUom`.
5. **The item popup's 200-row cap.** `IvStockMasterSearchPopup.LoadAsync` loads one page of 200 active rows; the grid search filters that page client-side. That is pre-existing, not introduced here, but it means a wide catalogue is already only partly reachable. Worth a separate ticket; do not fold a paging redesign into this plan.
6. **Scope of the width work.** `IvCodeComboBox` appears in 18 files across Inventory, Purchase and Sales. This plan adds the parameter, uses it on the converted fields, and remediates the Sales mismatches (register rows 1 and 15). The Purchase/Inventory call sites are audited by the same `MAX(LEN(...))` pre-check and, if they show the same defect, get the same one-line fix — but they are not a precondition for this plan.

## 11. Decision register — production approval gate

Phase 0 does not start, and this plan is not approved for production, until every row below is signed.

| # | Decision | Owner | Status | Lands in |
|---|---|---|---|---|
| 1 | Width policy — scope pickers to the destination width, or widen the columns (`SaItemCust.ICode` 20 vs `IvStockMaster.ICode` 30; `SaDisGroupItem.IClass` 10 vs `IvClass.IClassCode` 30; `SellingUOM`/`UOM` 5 vs `MsUom.UOMCode` 10; `Currency` 5 vs `SaCurrency.CurrCode` 20) | Product owner | **BLOCKED — needs owner** | Phase 0 step 4 |
| 2 | Company isolation = the six clauses in section 3.2, with clause 5 tolerance mandatory | Architecture | DECIDED | section 3.2 |
| 2a | Clause 5 is a **snapshot string comparison that never reads another company's data**; the residual cross-company case is a count-only data-quality report, not a rejection | Architecture / Security | DECIDED | section 3.2.1 |
| 3 | Active/orphan behaviour = section 3.1; a retired persisted value is never blanked | Product owner | DECIDED | section 3.1 |
| 4 | Lookup failure must be a visible error, never an empty list | Product owner | DECIDED | section 3.4 |
| 5 | Descriptions **and** names are derived server-side (`IvCustPrice.IDesc`, `SaItemCust.IDesc` from `IvStockMaster`; `SaLMW.CustName` from `SaCust`), never trusted from the client | Architecture | DECIDED | section 3.7 |
| 6 | `SaCust.CreditTerm` stays a documented single-option stub | Product owner | DECIDED | step 22 |
| 7 | `SaDisGroup.GroupStatus` vocabulary read from live data before the option list is written | Engineering | DECIDED + P0 verify | step 17 |
| 8 | `SaDisGroup.DiscountType` — dropdown only if identical to `SaDiscountSlotTypes`; otherwise stays free text | Engineering | DECIDED + P0 verify | step 17 |
| 9 | Sales-rep and customer lookups come from new **ungated** `ISaCustLookupService` members, not from `ISaSalesRefService.ListSalesRepsAsync` or `ISaCustService.GetAsync` | Architecture | DECIDED | Phase 0 step 1 |
| 10 | `SaDisGroup.PayCode` is validated by querying `IvMsCodes` on the write path; `SaSalesRefService`'s constructor is not changed | Architecture | DECIDED | step 15 |
| 11 | `SaLMW.LicenseType` stays free text unless a vocabulary is confirmed | Product owner | DECIDED | section 9 |
| 12 | No menu, permission or `menus.xml` change is required by this plan | Architecture | DECIDED | Phase 4 step 24 |
| 13 | The section 7 matrix (A–N) is the acceptance suite | Product owner | DECIDED | section 7 |
| 14 | Code-combo width filtering uses an additive `MaxCodeLength` on `IvCodeComboBox` plus one `LookupCodePolicy` — no per-screen length logic | Architecture | DECIDED | section 3.5.2, Phase 0 step 3a |
| 15 | The eight pre-existing `SaCust` width mismatches (and `PoSupplier.AreaCode`) are fixed in this release, or explicitly scheduled with a reason | Product owner | **BLOCKED — needs owner** | section 3.5.3, Phase 0 steps 3b–3c |
| 16 | Server-side length validation is added for every converted field, so an over-width value cannot reach the database | Architecture | DECIDED | section 3.5.4, Phase 0 step 3b |
| 17 | The filter → append → hint order is owned by `IvCodeComboBox`, not by individual pages | Architecture | DECIDED | section 3.1.1 |
| 18 | Assignment lists keep the `ListXxxForAssignmentAsync(CancellationToken)` shape; the current-value contract lives on the fixed-signature append helper | Architecture | DECIDED | section 3.1.1 |
| 19 | `SaLMW.CustName` is derived from `SaCust`, not accepted from the DTO | Architecture | DECIDED | section 3.7, step 16 |
| 20 | `SaDisGroup.GroupStatus` — derived / read-only (recommended) or a dropdown over the live vocabulary | Product owner | **BLOCKED — needs owner** | section 3.8, step 17 |
| 21 | `SaDisGroup.GroupStatus` and `SaDisGroup.DiscountType` get a server-side membership check, whatever row 20 decides | Architecture | DECIDED | section 3.9, step 17 |

## 12. Production-approval checklist

- [ ] Every register row above is signed or explicitly deferred with a reason.
- [ ] Phase 0 steps 3 and 4 are recorded in this file (plumbing done, live `MAX(LEN(...))` results captured).
- [ ] The section 7 matrix passes, including D, E, F, I, J, L and N.
- [ ] SQL Server concurrency suite ran with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` (not silently skipped).
- [ ] Orphan smoke (verification 4) and failure smoke (verification 5) recorded on every converted screen.
- [ ] Regression smoke (verification 7) recorded for the shipped `SaDisGroup` / `SaDisCust` / `SaCustPriceList` screens.

## 13. Out of scope (recorded so the boundary is explicit)

- `SaCustTypeList`, `SaCustGroupList`, `SaCustSubGroupList`, `SaAreaList`, `SaCountryList`, `SaCurrencyList`, `SaCurrRateList`, `SaPaymentTermList`, `SaShipViaList`, `SaSOTypeList`, `SaCommentList`, `SaShippingLeadTimeList` — the `Code` box is the master's own key, not a reference.
- `SaCustPriceList.razor` — read-only listing; its price-group filter is already a combo.
- GL code and Tax GL code — no GL account master exists in Blazor; free text is consistent with `MsDept`, `IvStockMaster` and `PoSupplier`.
- `SaCustEntry` field-level audit, `VIEW_PRICE` masking, price precedence and the SO/Invoice price-resolution consumer — separate initiatives (`plans/sales-item-family-v2-plan.md`).
- Any key widening or column re-typing. If section 11 row 1 chooses widening, that is a separate DBA migration with its own plan, backup and reversal script.
