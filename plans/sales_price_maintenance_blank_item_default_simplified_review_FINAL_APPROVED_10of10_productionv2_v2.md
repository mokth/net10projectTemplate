# Sales Price Maintenance — Blank Item Default Initialization + Simplified Review Flow
## FINAL APPROVED FOR IMPLEMENTATION — AI Coding Agent Plan V2

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Repo HEAD re-verified:** `dbe068fa8152433a7bfaeaf4792d612301d5b164`  
**Review date:** 2026-10-10  
**Status:** **FINAL APPROVED FOR IMPLEMENTATION**  
**Score:** **10 / 10 — repo-verified**  
**Change type:** Targeted Price Maintenance correctness fix + UI simplification  
**Database migration:** **NO**  
**Runtime Sales pricing-engine redesign:** **NO**  
**Posting / rollback / costing / tax / discount change:** **NO**

---

# 0. Final implementation verdict

Implement this fix.

The current `productionv2` code already has the correct backend foundation:

```text
IvStockMaster.SellingPrice = null
        ↓
Preview with BaselinePrice = null
        ↓
Apply validates baseline + RowVersion
        ↓
IvStockMaster.SellingPrice = Proposed Price
```

The problem is not the Item Default Apply path.

The main issues are:

1. the UI makes a blank Item Default look impossible to update when a relative adjustment cannot calculate it;
2. the user currently sees two confusing actions:
   - `CALCULATE NEW PRICES`
   - `PREVIEW CHANGES`
3. Item Default review UOM currently uses only `SellingUom` instead of runtime-compatible:
   - `SellingUom` when populated;
   - otherwise `StdUom`;
4. after server Preview, generic `"New price is required."` messaging hides the more useful `"Needs initial price"` guidance;
5. some UI text still says `Preview` even though the desired operator workflow is `Review Changes`.

The final operator workflow must be:

```text
LOAD ITEMS
    ↓
Select rows
    ↓
Choose adjustment
    ↓
REVIEW CHANGES
    ↓
Enter initial Proposed Price only where required
    ↓
REVIEW CHANGES again
    ↓
APPLY PRICE CHANGE
```

---

# 1. Non-negotiable business rules

## 1.1 Blank Item Default is maintainable

For:

```text
Target = Item Default
IvStockMaster.SellingPrice = null
```

the row must remain selectable and editable.

It may be initialized by:

```text
SET_PRICE
manual Proposed Price
Excel/import New Price
```

A blank Item Default is **not an invalid item**.

It means:

```text
the master selling price has not been initialized yet
```

---

## 1.2 Relative adjustments still require an existing price

These methods:

```text
INCREASE_PERCENT
DECREASE_PERCENT
INCREASE_AMOUNT
DECREASE_AMOUNT
```

must continue requiring an existing Current Price for automatic calculation.

Never do:

```text
blank = 0
```

and never calculate:

```text
blank + 10%
```

or:

```text
blank + RM10
```

The operator must explicitly provide the initial Proposed Price.

---

## 1.3 Sales transaction entry must NOT update Item Master

This fix applies only to:

```text
/sales/pricing/review
Sales Price Maintenance
```

Do not change:

```text
Quotation
Sales Order
Delivery Order
Invoice
CN / DN
```

so a manually entered transaction price writes `IvStockMaster.SellingPrice`.

Transaction pricing and master-price maintenance remain separate responsibilities.

---

# 2. Current repo facts — verified

Relevant files:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs

ErpWeb.Core/Sales/SaPriceAdjustmentCalculator.cs
ErpWeb.Core/Sales/SaPriceMaintenanceContracts.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.Apply.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.Workbook.cs

ErpWeb.Tests/Sales/Master/SaPriceAdjustmentCalculatorTests.cs
ErpWeb.Tests/Sales/Master/SaPriceMaintenanceServiceTests.cs
```

---

# 3. Root cause verification

## 3.1 Apply already supports null → price

Current `ApplyItemDefaultsAsync(...)`:

```text
loads IvStockMaster
validates RowVersion
validates BaselinePrice
validates NewPrice
updates SellingPrice
writes audit
```

It does not require old `SellingPrice` to be non-null.

Therefore:

> **Do not redesign Item Default Apply.**

---

## 3.2 Calculator behavior is already correct

Existing tests verify:

```text
Set_price_allows_null_old_price_and_rounds
```

and:

```text
Percent_and_amount_methods_block_null_old_price
```

Keep both behaviors.

Do not weaken `SaPriceAdjustmentCalculator`.

---

# 4. Required Item Default behavior matrix

| Current Item Default | Method | Automatic Proposed | Result |
|---|---|---:|---|
| Existing | SET_PRICE | Yes | Ready |
| Existing | Increase/Decrease % | Yes | Ready |
| Existing | Increase/Decrease Amount | Yes | Ready |
| Blank | SET_PRICE | Yes | Ready |
| Blank | Relative % | No | Needs Price |
| Blank | Relative Amount | No | Needs Price |
| Blank | Manual Proposed | N/A | Ready after Review |
| Blank | Imported Proposed | N/A | Ready after Review |

---

# 5. Simplify UI to one Review action

Current UI:

```text
CALCULATE NEW PRICES
PREVIEW CHANGES
```

Remove the Calculate button.

Replace Preview with:

```text
REVIEW CHANGES
```

Keep final commit action:

```text
APPLY PRICE CHANGE
```

Required two-stage safety:

```text
REVIEW CHANGES
→ APPLY PRICE CHANGE
```

Do not merge Apply into Review.

---

# 6. Why this simplification is safe in the current repo

Current `PreviewChangesAsync()` already does:

```text
selected rows
→ find rows where ProposedPrice is blank
→ CalculateProposals(...)
→ PreviewAsync()
```

Therefore the separate `CalculateChangesAsync()` button is redundant.

The simplification does not remove validation.

It only removes duplicate user actions.

---

# 7. Exact Razor changes

File:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
```

Remove:

```razor
<DxButton RenderStyle="ButtonRenderStyle.Secondary"
          Text="CALCULATE NEW PRICES"
          IconCssClass="fa-solid fa-calculator"
          Click="@CalculateChangesAsync"
          ... />
```

Replace:

```razor
Text="PREVIEW CHANGES"
Click="@PreviewChangesAsync"
```

with:

```razor
Text="REVIEW CHANGES"
Click="@ReviewChangesAsync"
Enabled="@(!IsBusy && HasRows && SelectedCount > 0)"
```

Use:

```text
RenderStyle = Primary
Icon = fa-solid fa-list-check
```

---

# 8. Exact code-behind changes

File:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs
```

Rename:

```text
PreviewChangesAsync()
→ ReviewChangesAsync()
```

Remove:

```text
CalculateChangesAsync()
```

after confirming no remaining references.

Keep:

```text
CalculateProposals(...)
PreviewAsync()
```

as internal implementation helpers.

Do not rename Core `PreviewAsync(...)`.

---

# 9. ReviewChangesAsync contract

Required flow:

```text
1. return if busy
2. clear ErrorMessage / LastPreview / LastApply / ImportPreview
3. get selected rows
4. if no selected rows → show concise error
5. identify selected rows where ProposedPrice is blank
6. calculate only those blank proposals
7. preserve all manually entered ProposedPrice values
8. call server PreviewAsync()
```

Never recalculate a row where:

```text
ProposedPrice.HasValue == true
```

This is critical.

---

# 10. Preserve manually entered Proposed Price

Example:

```text
CurrentPrice = null
Method = INCREASE_PERCENT
```

First Review:

```text
Proposed = blank
Needs Price
```

User enters:

```text
150.0000
```

Second Review must preserve:

```text
150.0000
```

Do not overwrite it using the adjustment method.

---

# 11. Friendlier blank-price calculation behavior

In `CalculateProposals(...)` or a small UI helper before generic calculation:

```text
if Target = Item Default
and CurrentPrice is null
and ProposedPrice is blank
and method != SET_PRICE
```

do:

```text
ProposedPrice = null
Status = BLOCKED
Warning = "No current Item Default price. Enter the initial selling price in Proposed, then Review Changes again."
```

Do not call the generic calculator for this UI-specific case.

For all other rows:

```text
SaPriceAdjustmentCalculator.Calculate(...)
```

remains authoritative.

---

# 12. Server Preview must return the same friendly message

Current server `PreviewAsync(...)` currently uses:

```text
"New price is required."
```

whenever `selection.NewPrice` is null.

Because server Preview replaces the grid rows with server result rows, the local friendly warning would otherwise disappear.

Therefore update:

```text
ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
PreviewAsync(...)
```

so this specific case produces the actionable warning:

```text
Target = Item Default
CurrentPrice = null
NewPrice = null
AdjustmentMethod != SET_PRICE
```

→

```text
"No current Item Default price. Enter the initial selling price in Proposed, then Review Changes again."
```

All other missing-New-Price cases may keep:

```text
"New price is required."
```

This changes wording only, not validation semantics.

---

# 13. Backend status token remains unchanged

Do not add a new DB or Core status token.

Continue using:

```text
READY
UNCHANGED
BLOCKED
```

The UI may display:

```text
READY       → Ready
UNCHANGED   → No Change
specific blank-price BLOCKED → Needs Price
other BLOCKED → Check Warning
```

Do not change persisted/audit contracts.

---

# 14. Status display must survive server Review

After Review, current UI does:

```text
Rows = result.Data.Rows.ToList()
SelectedDataItems = Rows.Where(x => x.Selected)...
```

Therefore status presentation must be based on the server-returned row after Review.

Recommended Razor helper:

```text
DisplayStatus(row)
```

Rules:

```text
if row.Status == BLOCKED
and IsItemDefault
and row.CurrentPrice == null
and row.ProposedPrice == null
and method != SET_PRICE
    → "Needs Price"

else if BLOCKED
    → "Check Warning"

READY
    → "Ready"

UNCHANGED
    → "No Change"
```

---

# 15. Review summary wording

Current status message:

```text
Preview ready: X changing, Y unchanged, Z blocked.
```

Change operator-facing text to:

```text
Review ready: X changing, Y unchanged, Z need attention.
```

Do not change:

```text
SaPricePreviewSummary.Blocked
```

Core field names remain unchanged.

---

# 16. Replace remaining Preview wording in UI

Update these operator-facing messages:

```text
"Run Preview before applying..."
→ "Run Review Changes before applying..."

"Run Preview changes before applying."
→ "Run Review Changes before applying."

"Resolve the blocked rows shown in the preview..."
→ "Resolve the rows that need attention, then Review Changes again."

"Apply becomes available after a successful preview..."
→ "Apply becomes available after a successful review..."
```

Import message:

```text
"Imported ... Review the staged values, then preview."
→
"Imported ... Review the staged values, then click Review Changes."
```

Keep Core/service method names as Preview.

---

# 17. Item Default search must include blank SellingPrice

Current `SearchItemDefaultsAsync(...)` correctly has no:

```text
SellingPrice != null
```

filter.

Preserve this.

Do not exclude blank Item Default rows.

---

# 18. Effective Item Default UOM — mandatory fix

Current Price Maintenance uses:

```csharp
Uom = x.SellingUom
```

but runtime Item Default pricing uses:

```text
SellingUom when populated
otherwise StdUom
```

Current runtime source:

```text
SaItemFamilyPriceResolver.SelectItemDefault(...)
```

normalizes exactly this fallback.

Price Maintenance must match it.

---

# 19. Update BOTH Item Default read paths

Update:

```text
SearchItemDefaultsAsync(...)
LoadCurrentRowsAsync(...)
```

to produce:

```text
EffectiveUom =
SellingUom when nonblank
otherwise StdUom
```

The Search row and server Preview reload must return the same UOM.

Do not fix only the first search path.

---

# 20. EF/provider safety for effective UOM

Tests use SQLite while production uses SQL Server.

Use one of these safe approaches:

### Preferred if provider translation passes

```csharp
Uom = !string.IsNullOrWhiteSpace(x.SellingUom)
    ? x.SellingUom
    : x.StdUom
```

### If translation is provider-sensitive

Select bounded rows with both:

```text
SellingUom
StdUom
```

then resolve effective UOM in memory.

The review is already capped by:

```text
SaPriceMaintenanceLimits.MaxReviewRows
```

Do not introduce an unbounded client-side materialization.

---

# 21. Blank effective UOM

If both:

```text
SellingUom
StdUom
```

are blank:

```text
UOM = blank / —
```

Do not invent a UOM.

Recommended warning presentation for Item Default row:

```text
"Item has no Selling UOM or Standard UOM. Fix Item Master UOM before relying on this Item Default price."
```

This UOM warning is independent of whether a Proposed Price can technically be stored.

Do not silently block price maintenance solely because the UOM is blank unless existing business validation already requires it.

---

# 22. Item Default audit UOM

Current `ApplyItemDefaultsAsync(...)` audit writes:

```text
OldUom = SellingUom
NewUom = SellingUom
```

Change audit display context to runtime-compatible effective UOM:

```text
SellingUom when nonblank
otherwise StdUom
```

Do not change actual master UOM fields.

This keeps:

```text
Price Maintenance grid
runtime Item Default source
price-change history
```

consistent.

---

# 23. SET_PRICE + blank Item Default

Example:

```text
FG001 Current = blank
FG002 Current = blank
FG003 Current = 90
```

Method:

```text
SET_PRICE
Value = 100
```

Click:

```text
REVIEW CHANGES
```

Expected:

```text
FG001 Proposed = 100 Ready
FG002 Proposed = 100 Ready
FG003 Proposed = 100 Ready / No Change if already 100
```

No blank-current row is blocked.

---

# 24. Mixed relative-adjustment example

Rows:

```text
FG001 Current = 100
FG002 Current = 200
FG003 Current = blank
```

Method:

```text
Increase 10%
```

First Review:

| Item | Current | Proposed | Display Status |
|---|---:|---:|---|
| FG001 | 100.0000 | 110.0000 | Ready |
| FG002 | 200.0000 | 220.0000 | Ready |
| FG003 | — | — | Needs Price |

Warning for FG003:

```text
No current Item Default price. Enter the initial selling price in Proposed, then Review Changes again.
```

User enters:

```text
FG003 Proposed = 150
```

Second Review:

| Item | Current | Proposed | Status |
|---|---:|---:|---|
| FG001 | 100.0000 | 110.0000 | Ready |
| FG002 | 200.0000 | 220.0000 | Ready |
| FG003 | — | 150.0000 | Ready |

Apply:

```text
FG001 SellingPrice = 110
FG002 SellingPrice = 220
FG003 SellingPrice = 150
```

---

# 25. Manual Proposed Price change behavior

Current `OnProposedPriceChangedAsync(...)` already:

```text
invalidates LastPreview
clears LastApply
clears ImportPreview
sets status based on Proposed
updates difference
```

Preserve this.

When a user types a positive Proposed Price for a blank Item Default:

```text
row.Warning = null
row.Status = Ready
LastPreview = null
```

Then user must click:

```text
REVIEW CHANGES
```

again before Apply.

Correct.

---

# 26. Apply safety — describe the ACTUAL repo mechanism

The current repo does **not** use a separate loaded-scope fingerprint or preview fingerprint.

Do not add one as part of this fix.

Current real safety flow:

```text
UI Review
    ↓
server PreviewAsync
    ↓
baseline + RowVersion validation

UI Apply
    ↓
server ApplyAsync
    ↓
ApplyAsync RE-RUNS PreviewAsync using the submitted selections
    ↓
blocks if any selected row is stale/invalid
    ↓
target Apply method starts transaction
    ↓
RowVersion + baseline validated again
    ↓
official master updated
```

This is the safety model to preserve.

---

# 27. Null baseline concurrency — mandatory

A blank price baseline is still meaningful.

Review:

```text
BaselinePrice = null
```

If another user changes:

```text
SellingPrice null → 120
```

before Apply, then:

```text
ValidateSelectionBaseline(...)
```

and/or:

```text
ValidateBaselinePrice(...)
```

must reject the change.

Do not overwrite the new 120.

---

# 28. Apply remains blocked until clean Review

Do not change:

```text
LastPreview required
Changing > 0
no blocked rows
EDIT permission
Reason required
```

Apply confirmation remains mandatory.

The UX simplification is only:

```text
Calculate + Preview
→ Review
```

not removal of safety gates.

---

# 29. Apply uses selected Review rows only

Current server Preview returns rows for the submitted selections.

Current UI then replaces:

```text
Rows = preview.Rows
```

Therefore after Review, the grid effectively becomes the reviewed selected set.

Do not introduce logic assuming unselected original search rows are still present after Review.

All `HasBlockedRows`, Apply and status logic must operate correctly on the reviewed row set.

---

# 30. Difference fields for first-time price

For:

```text
CurrentPrice = null
ProposedPrice = 150
```

keep:

```text
DifferenceAmount = null
DifferencePercent = null
```

Display:

```text
—
```

Do not treat blank current price as zero.

Current Core/UI logic already supports this.

---

# 31. Zero is NOT blank

Preserve distinction:

```text
null
```

versus:

```text
0
```

Do not add a null/zero normalization in this fix.

Existing calculator and runtime semantics remain unchanged.

---

# 32. Excel workbook behavior — preserve schema

Current `BuildReviewWorkbookAsync(...)` also runs the calculator.

For blank Item Default + relative adjustment:

```text
Current Price = blank
New Price = blank
```

This is acceptable.

The user can manually enter:

```text
New Price
Selected = Yes
```

and import it.

Do not change workbook headers or template version just for this fix.

---

# 33. Excel import — blank current + positive new price

Current `ParseImportAsync(...)` validates:

```text
Current Price matches staged CurrentPrice
New Price is decimal or blank
New Price >= 0
```

It already supports:

```text
staged CurrentPrice = null
workbook Current Price = blank
workbook New Price = 150
```

Preserve this.

Add regression coverage.

---

# 34. Workbook export user-friendliness

Do not add a new workbook warning column.

Instead document/test:

```text
relative adjustment + blank current
→ exported New Price remains blank intentionally
```

User enters initial New Price manually.

This avoids a template migration for a small maintenance UX fix.

---

# 35. Price List and Customer Special behavior — no change

Do not change:

```text
Price List row identity
Price List quantity bands
Price List validity dates
Price List currency
Price List Schedule From Date
Price List overlap/successor logic
Customer Special MOQ
Customer Special currency
Customer Special identity
```

Do not auto-create missing:

```text
Price List rows
Customer Special rows
```

This fix is about blank Item Default initialization and review UX.

---

# 36. Files to modify

Mandatory:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs

ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.Apply.cs

ErpWeb.Tests/Sales/Master/SaPriceMaintenanceServiceTests.cs
```

Possible test-only update:

```text
ErpWeb.Tests/Sales/Master/SaPriceAdjustmentCalculatorTests.cs
```

if additional explicit regression cases are useful.

---

# 37. Files that should NOT need behavioral redesign

Do not redesign:

```text
ErpWeb.Core/Sales/SaPriceAdjustmentCalculator.cs
ErpWeb.Core/Sales/SaPriceMaintenanceContracts.cs
ErpWeb.Core/Sales/ISaPriceMaintenanceService.cs
runtime Sales price resolver
```

`SaPriceMaintenanceService.Workbook.cs` normally needs **tests only**, not production changes, unless an implementation issue is discovered while adding workbook regression tests.

No DB migration.

---

# 38. UI copy checklist

Replace operator-facing wording consistently:

```text
CALCULATE NEW PRICES
→ remove

PREVIEW CHANGES
→ REVIEW CHANGES

Preview ready
→ Review ready

Run Preview...
→ Run Review Changes...

successful preview
→ successful review

blocked
→ needs attention
```

Do not rename Core method/class names.

---

# 39. Tests — existing calculator contract

Must remain green:

```text
Set_price_allows_null_old_price_and_rounds
Percent_and_amount_methods_block_null_old_price
```

These two tests are important rejection gates.

---

# 40. Tests — Item Default search

Add seeded active item:

```text
ICode = "BLANK-PRICE"
SellingPrice = null
SellingUom = null
StdUom = "PCS"
```

Search:

```text
Target = ItemDefault
LoadAllActiveItems = true
```

Expected:

```text
row included
CurrentPrice = null
Uom = "PCS"
```

Also verify inactive blank item is still excluded by LoadAllActiveItems.

---

# 41. Tests — SellingUom wins

Seed:

```text
SellingUom = "BOX"
StdUom = "PCS"
```

Expected:

```text
Uom = "BOX"
```

---

# 42. Tests — Preview first-time Item Default

Selection:

```text
BaselinePrice = null
NewPrice = 150
```

Expected server Preview:

```text
Status = READY
Changing = 1
Blocked = 0
```

---

# 43. Tests — server Needs Price warning

Selection:

```text
Target = ItemDefault
CurrentPrice = null
NewPrice = null
AdjustmentMethod = INCREASE_PERCENT
```

Expected:

```text
Status = BLOCKED
Warning contains "Enter the initial selling price"
```

Do not return only generic `"New price is required."` for this case.

---

# 44. Tests — Apply initializes blank SellingPrice

Before:

```text
SellingPrice = null
```

Apply:

```text
BaselinePrice = null
NewPrice = 150
```

Expected:

```text
SellingPrice = 150
ChangedRowCount = 1
audit OldPrice = null
audit NewPrice = 150
```

---

# 45. Tests — null baseline concurrency

Review baseline:

```text
null
```

Modify DB before Apply:

```text
SellingPrice = 120
```

Expected:

```text
Apply fails
120 remains
no overwrite
```

---

# 46. Tests — effective audit UOM

Seed:

```text
SellingUom = null
StdUom = "PCS"
SellingPrice = null
```

Apply new Item Default.

Expected audit:

```text
OldUom = PCS
NewUom = PCS
```

---

# 47. Tests — workbook round-trip

Add service tests if practical in the existing test class:

## Relative export with blank Item Default

Expected:

```text
Current Price blank
New Price blank
```

No exception.

## Import manually entered first price

Workbook row:

```text
Current Price blank
New Price 75
Selected Yes
```

Expected:

```text
ParseImportAsync IsValid = true
update NewPrice = 75
```

No template-version change.

---

# 48. Mandatory manual UI acceptance

1. Load Item Default rows containing existing and blank prices.
2. Verify blank price rows are visible.
3. Verify UOM falls back to StdUom.
4. Verify only one action is shown in Adjustment:
   - `REVIEW CHANGES`.
5. Verify `CALCULATE NEW PRICES` no longer exists.
6. Review disabled when no rows selected.
7. SET_PRICE + blank item auto-fills Proposed.
8. Increase % + blank item shows `Needs Price`.
9. Type initial Proposed manually.
10. Confirm typed value is not overwritten.
11. Review again.
12. Confirm row becomes Ready.
13. Enter reason.
14. Apply.
15. Verify official Item Master SellingPrice changed.
16. Verify price history/audit batch exists.
17. Verify Price List and Customer Special workflows still work.

---

# 49. Build and regression gate

Mandatory:

```powershell
dotnet build ErpWeb.slnx --nologo -v:q
```

Then:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --nologo
```

Razor compilation is mandatory.

---

# 50. AI Coding Agent implementation order

## Phase 1 — tests first

Add:

```text
blank-price search test
effective UOM tests
preview first-price test
Needs Price warning test
apply null→price test
null-baseline concurrency test
audit UOM test
workbook round-trip tests
```

Confirm new tests fail for the intended missing behavior only.

---

## Phase 2 — effective UOM

Update:

```text
SearchItemDefaultsAsync
LoadCurrentRowsAsync
Item Default audit UOM
```

Run service tests.

---

## Phase 3 — server friendly warning

Update `PreviewAsync(...)` message for:

```text
ItemDefault + CurrentPrice null + NewPrice null + relative method
```

No validation relaxation.

Run service tests.

---

## Phase 4 — simplified UI

Remove Calculate button.

Rename:

```text
PreviewChangesAsync
→ ReviewChangesAsync
```

Remove `CalculateChangesAsync()`.

Update UI copy.

Add `Needs Price` display mapping.

---

## Phase 5 — manual UI validation

Verify mixed selection:

```text
priced rows + blank rows
```

with:

```text
SET_PRICE
relative %
relative amount
manual Proposed
```

---

## Phase 6 — full regression

Run full build/tests.

Verify:

```text
Price List unchanged
Customer Special unchanged
runtime Sales pricing unchanged
no migration
```

---

# 51. AI Agent stop conditions

Stop and report rather than guessing if current branch changes such that:

1. `ApplyAsync(...)` no longer re-runs `PreviewAsync(...)`;
2. `ApplyItemDefaultsAsync(...)` no longer validates baseline/RowVersion;
3. `SaPriceAdjustmentCalculator` semantics changed;
4. Item Default runtime no longer uses SellingUom→StdUom fallback;
5. workbook template/version changed independently;
6. Price Maintenance page no longer replaces Rows with Preview result rows;
7. implementation would require changing runtime Sales pricing;
8. implementation would require a DB migration.

---

# 52. Rejection conditions

Reject implementation if:

- blank Item Default rows disappear from search;
- blank Item Default cannot accept SET_PRICE;
- manually entered Proposed Price is overwritten;
- relative methods treat blank as zero;
- Calculate button remains;
- Review button does not run server Preview;
- Apply can run without successful Review;
- null baseline concurrency becomes weaker;
- effective UOM is fixed in Search but not Preview reload;
- audit still shows blank UOM when StdUom exists;
- Excel template is unnecessarily changed;
- Price List / Customer Special logic changes;
- runtime pricing resolver changes;
- DB migration is introduced.

---

# 53. Definition of Done

## Blank Item Default

- [ ] visible in search;
- [ ] SET_PRICE works;
- [ ] manual Proposed works;
- [ ] import works;
- [ ] Apply updates `IvStockMaster.SellingPrice`;
- [ ] null baseline protected.

## Simplified UX

- [ ] Calculate button removed;
- [ ] Preview button renamed Review Changes;
- [ ] Review calculates missing proposals and calls server Preview;
- [ ] Apply remains separate;
- [ ] UI wording consistently says Review;
- [ ] blank relative row displays Needs Price.

## UOM

- [ ] SellingUom wins;
- [ ] StdUom fallback works;
- [ ] Search and Preview reload agree;
- [ ] audit uses effective UOM;
- [ ] no invented UOM.

## Safety

- [ ] Apply still re-runs server Preview;
- [ ] server baseline validation preserved;
- [ ] RowVersion validation preserved;
- [ ] transaction-level validation preserved;
- [ ] reason required;
- [ ] audit batch preserved.

## Regression

- [ ] calculator contract unchanged;
- [ ] workbook template unchanged;
- [ ] Price List unchanged;
- [ ] Customer Special unchanged;
- [ ] runtime Sales pricing unchanged;
- [ ] build green;
- [ ] tests green;
- [ ] no migration.

---

# 54. FINAL APPROVAL

## **FINAL APPROVED FOR IMPLEMENTATION — 10 / 10**

This V2 plan is verified against `productionv2` HEAD:

```text
dbe068fa8152433a7bfaeaf4792d612301d5b164
```

It is safe for an AI coding agent because it now matches the repo's actual implementation details:

```text
Preview replaces the grid with reviewed selected rows
Apply re-runs Preview server-side
baseline + RowVersion are checked before Apply
target Apply checks them again in the write transaction
SET_PRICE already supports null Current Price
relative methods correctly reject null arithmetic
Excel import already supports blank Current + manual New Price
runtime Item Default uses SellingUom → StdUom fallback
```

The fix therefore remains small, practical and additive.

**AI Coding Agent instruction:** implement only the blank Item Default usability fix, effective UOM consistency, friendly review messaging, and simplified Review Changes workflow. Preserve all existing runtime Sales pricing, Price List scheduling, Customer Special behavior, permissions, concurrency, audit, posting, tax, discount and costing logic.
