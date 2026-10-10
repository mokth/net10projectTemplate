# Sales Price Maintenance — Blank Item Default Initialization + Simplified Review Flow
## FINAL APPROVED FOR IMPLEMENTATION — AI Coding Agent Fix Plan

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Repo HEAD verified:** `dbe068fa8152433a7bfaeaf4792d612301d5b164`  
**Review date:** 2026-10-10  
**Status:** **FINAL APPROVED FOR IMPLEMENTATION**  
**Score:** **10 / 10**  
**Change type:** Targeted Price Maintenance bug fix + UI simplification  
**Database migration:** **NO**  
**Pricing engine redesign:** **NO**  
**Sales transaction pricing behavior change:** **NO**  
**Posting / costing / tax / discount change:** **NO**

---

# 0. Final implementation decision

Fix the current Sales Price Maintenance page so that:

1. **Item Default rows with no existing `IvStockMaster.SellingPrice` are still maintainable.**
2. A blank Item Default can be initialized by:
   - `SET_PRICE`;
   - manually typing Proposed Price;
   - workbook/import Proposed Price.
3. Relative adjustments (`+/- %`, `+/- Amount`) still require an existing Current Price for automatic calculation.
4. A blank-price row under a relative adjustment must **not look like a dead-end system error**. Show it as `Needs initial price` and allow the user to type Proposed Price manually.
5. Remove the confusing two-button workflow:
   - `CALCULATE NEW PRICES`
   - `PREVIEW CHANGES`

   and replace it with one action:

   - `REVIEW CHANGES`
6. Keep `APPLY PRICE CHANGE` as a separate protected action.
7. Fix Item Default UOM display/context to use:
   - `SellingUom` when populated;
   - otherwise `StdUom`.

Target UX:

```text
Load Items
    ↓
Select rows
    ↓
Choose adjustment
    ↓
REVIEW CHANGES
    ↓
Review Current / Proposed / Difference / Warnings
    ↓
APPLY PRICE CHANGE
```

---

# 1. Current repo findings — verified

Current Price Maintenance files:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs

ErpWeb.Core/Sales/SaPriceAdjustmentCalculator.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.Apply.cs
ErpWeb.Core/Sales/SaPriceMaintenanceContracts.cs
```

Existing tests:

```text
ErpWeb.Tests/Sales/Master/SaPriceAdjustmentCalculatorTests.cs
ErpWeb.Tests/Sales/Master/SaPriceMaintenanceServiceTests.cs
```

---

# 2. Root cause — blank Item Default is NOT blocked by Apply

The backend Item Default Apply path already supports:

```text
old IvStockMaster.SellingPrice = null
new Proposed Price = value
```

Current `ApplyItemDefaultsAsync(...)` validates baseline/row version/new price and then assigns:

```text
item.SellingPrice = NewPrice
```

Therefore:

> **Do not redesign Apply. The current Item Default write path already supports null → new value.**

---

# 3. Actual block — relative calculator methods

Current `SaPriceAdjustmentCalculator.Calculate(...)` correctly allows:

```text
SET_PRICE + oldPrice=null
```

Existing unit test already verifies:

```text
Set_price_allows_null_old_price_and_rounds
```

Current calculator correctly blocks null old price for:

```text
INCREASE_PERCENT
DECREASE_PERCENT
INCREASE_AMOUNT
DECREASE_AMOUNT
```

Existing test:

```text
Percent_and_amount_methods_block_null_old_price
```

This is correct.

Do **not** change the calculator to treat blank as zero.

---

# 4. Required business rule

For **Target = Item Default**:

| Current Item Default | Method | Automatic Proposed Price | User action |
|---|---|---:|---|
| Existing price | Set Price | Yes | Review |
| Existing price | +/- % | Yes | Review |
| Existing price | +/- Amount | Yes | Review |
| Blank | Set Price | Yes | Review |
| Blank | +/- % | No | Enter initial Proposed Price |
| Blank | +/- Amount | No | Enter initial Proposed Price |
| Blank | Manual Proposed | Yes | Review |
| Blank | Import Proposed | Yes | Review |

A blank Item Default must never be excluded merely because none of the runtime Sales price sources currently produces a usable price.

---

# 5. Do NOT update Item Master from Sales transaction entry

This fix applies only to:

```text
Sales Price Maintenance
```

Do not change Quotation / SO / Invoice / CN/DN so typing a transaction price silently writes Item Master.

Correct separation:

```text
Sales transaction has no price
→ existing blocking / authorised transaction override rules

Price Maintenance Item Default is blank
→ administrator may initialize IvStockMaster.SellingPrice
```

---

# 6. Item Default search must include blank prices

Current Item Default search already loads from `IvStockMasters` without requiring `SellingPrice` to be non-null.

Preserve this.

Do not add any filter such as:

```csharp
.Where(x => x.SellingPrice != null)
```

"Load all active items" must continue to include active items with `SellingPrice = null`.

---

# 7. Fix Item Default effective UOM

Current implementation uses:

```csharp
Uom = x.SellingUom
```

in both:

```text
SearchItemDefaultsAsync(...)
LoadCurrentRowsAsync(...)
```

Change to:

```text
SellingUom when nonblank
otherwise StdUom
```

Concept:

```csharp
Uom = !string.IsNullOrWhiteSpace(x.SellingUom)
    ? x.SellingUom
    : x.StdUom
```

Do not modify the actual item master UOM fields.

---

# 8. Blank UOM safety

If both `SellingUom` and `StdUom` are blank:

```text
UOM: —
Warning: Item has no selling/standard UOM. Fix Item Master UOM before relying on this Item Default price.
```

Do not invent a UOM.

---

# 9. Simplify the adjustment actions

Current UI has:

```text
CALCULATE NEW PRICES
PREVIEW CHANGES
```

This is redundant because current `PreviewChangesAsync()` already calculates blank proposals before server preview.

Required UI:

```text
[ REVIEW CHANGES ]
```

Then final:

```text
[ APPLY PRICE CHANGE ]
```

Remove the separate Calculate button completely.

---

# 10. Rename handler for clarity

Recommended:

```text
PreviewChangesAsync()
→ ReviewChangesAsync()
```

Internally it may still call:

```text
PreviewAsync()
```

Do not rename Core service/API contracts just for UI wording.

---

# 11. Remove obsolete Calculate button handler

Current:

```text
CalculateChangesAsync()
```

should be removed if no longer referenced.

Keep:

```text
CalculateProposals(...)
```

as an internal helper.

Target:

```text
ReviewChangesAsync()
    → calculate missing proposals
    → server PreviewAsync()
```

---

# 12. New Review Changes behavior

When user clicks `REVIEW CHANGES`:

1. Require at least one selected row.
2. Preserve any Proposed Price already entered by the user.
3. For selected rows with blank Proposed Price:
   - calculate automatically when possible;
   - blank Item Default + relative method => do not invent a price.
4. Existing-price rows calculate normally.
5. Blank Item Default + `SET_PRICE` calculates normally.
6. Blank Item Default + relative method becomes `Needs Price`.
7. Call existing server preview for validation.
8. Show review summary and warnings.

---

# 13. Never overwrite a user's manual Proposed Price

Example:

```text
Current = blank
Method = Increase 10%
```

First review:

```text
Proposed = blank
Status = Needs Price
```

User enters:

```text
150.0000
```

Second review must preserve `150.0000`.

Keep the current rule:

```text
only auto-calculate when ProposedPrice is blank
```

---

# 14. Friendly null-price status

For Item Default where:

```text
CurrentPrice == null
ProposedPrice == null
method != SET_PRICE
```

show:

```text
Status: Needs Price

Warning:
No current Item Default price. Enter the initial selling price in Proposed, then Review Changes again.
```

Do not leave only the calculator's technical error message.

---

# 15. Backend status compatibility

Do not add a DB/schema status.

Backend may continue using:

```text
BLOCKED
```

for unresolved rows.

UI may map the specific condition to:

```text
Needs Price
```

and other blocked rows to:

```text
Check Warning
```

---

# 16. Mixed-selection example

Items:

```text
FG001 Current = 100
FG002 Current = 200
FG003 Current = blank
```

Method:

```text
Increase 10%
```

Click `REVIEW CHANGES`.

Expected:

| Item | Current | Proposed | Status |
|---|---:|---:|---|
| FG001 | 100.0000 | 110.0000 | Ready |
| FG002 | 200.0000 | 220.0000 | Ready |
| FG003 | — | [editable] | Needs Price |

User enters:

```text
FG003 Proposed = 150
```

Review again.

Expected:

| Item | Current | Proposed | Status |
|---|---:|---:|---|
| FG001 | 100.0000 | 110.0000 | Ready |
| FG002 | 200.0000 | 220.0000 | Ready |
| FG003 | — | 150.0000 | Ready |

Apply writes:

```text
FG001 SellingPrice = 110
FG002 SellingPrice = 220
FG003 SellingPrice = 150
```

---

# 17. SET PRICE example for blank Item Default

Rows:

```text
FG001 Current = blank
FG002 Current = blank
FG003 Current = 90
```

Method:

```text
SET PRICE
Value = 100
```

Review:

```text
FG001 Proposed 100
FG002 Proposed 100
FG003 Proposed 100
```

Blank Current Price must not be blocked.

---

# 18. Manual Proposed Price path

Even if relative adjustment is selected, user may manually enter a Proposed Price for a blank Item Default.

`OnProposedPriceChangedAsync(...)` must:

```text
clear old warning
invalidate LastPreview
preserve typed Proposed Price
set row to reviewable state
```

Then `REVIEW CHANGES` uses the typed value.

---

# 19. Import path

For Item Default:

```text
Current = blank
Imported Proposed = 150
```

must be valid.

Do not require old/current price to be populated just because the Proposed Price came from import.

---

# 20. Concurrency safety — preserve

Do not weaken:

```text
RowVersion
BaselinePrice
scope/preview fingerprints
```

A blank baseline is a real baseline:

```text
BaselinePrice = null
```

If another user changes DB SellingPrice before Apply:

```text
expected null
actual 120
```

Apply must reject as stale.

Current `ValidateBaselinePrice(...)` already supports this.

---

# 21. Apply behavior — preserve Preview gate

Final flow remains:

```text
REVIEW CHANGES
→ successful server Preview
→ APPLY PRICE CHANGE
```

Do not bypass server Preview.

Apply remains disabled while selected rows still have unresolved issues.

---

# 22. Keep Apply separate

Do not merge Review and Apply.

Price-master changes are important enough to keep one final confirmation step.

The simplification is:

```text
3 actions → 2 actions
```

not:

```text
2 actions → 1 action
```

Final workflow:

```text
REVIEW CHANGES
APPLY PRICE CHANGE
```

---

# 23. UI copy updates

Adjustment section help:

```text
Choose an adjustment and select the rows to update. Review Changes will calculate available proposed prices and validate them before Apply.
```

When blank Item Defaults exist:

```text
Items without a current Item Default can still be initialized.
For percentage/amount adjustments, enter their initial Proposed price manually.
```

Keep wording short.

---

# 24. Button layout

Adjustment actions:

```text
<selected count>          [ REVIEW CHANGES ]
```

Button:

```text
Primary
Icon: fa-solid fa-list-check
Enabled: !IsBusy && HasRows && SelectedCount > 0
```

Remove the Calculate button.

---

# 25. Summary wording

UI may display:

```text
3 changing · 1 unchanged · 1 needs attention
```

instead of only:

```text
1 blocked
```

Underlying `Summary.Blocked` may remain unchanged.

---

# 26. Grid status wording

Recommended display mapping:

```text
READY
→ Ready

UNCHANGED
→ No Change

BLOCKED caused by blank Item Default + no Proposed under relative method
→ Needs Price

other BLOCKED
→ Check Warning
```

No backend token change.

---

# 27. Difference fields for initial price

For:

```text
CurrentPrice = null
ProposedPrice = 150
```

display:

```text
Difference = —
Change % = —
```

Do not treat blank as zero.

Current Core logic already supports this.

---

# 28. Zero is different from blank

Do not merge:

```text
CurrentPrice = null
```

with:

```text
CurrentPrice = 0
```

Do not add new zero restrictions in this fix.

---

# 29. Effective UOM must be consistent in both read paths

Update both:

```text
SearchItemDefaultsAsync(...)
LoadCurrentRowsAsync(...)
```

to use the same effective UOM rule.

Search and Preview/current reload must never disagree.

---

# 30. Item Default audit UOM

Current Item Default Apply audit uses `SellingUom`.

Change audit display context to effective UOM:

```text
SellingUom when nonblank
otherwise StdUom
```

for `OldUom` / `NewUom`.

Do not modify Item Master UOM values.

---

# 31. Files to modify

Mandatory:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs

ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.Apply.cs

ErpWeb.Tests/Sales/Master/SaPriceMaintenanceServiceTests.cs
```

Potential regression additions:

```text
ErpWeb.Tests/Sales/Master/SaPriceAdjustmentCalculatorTests.cs
```

The calculator itself should normally remain unchanged.

---

# 32. Files that should NOT need redesign

Do not redesign:

```text
SaPriceAdjustmentCalculator.cs
SaPriceMaintenanceContracts.cs
ISaPriceMaintenanceService.cs
runtime Sales price resolver
```

No DB migration.

---

# 33. Exact Razor changes

Remove:

```razor
<DxButton Text="CALCULATE NEW PRICES"
          Click="@CalculateChangesAsync" ... />
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

Keep `APPLY PRICE CHANGE`.

---

# 34. Exact code-behind changes

Recommended:

```text
PreviewChangesAsync
→ ReviewChangesAsync
```

Keep:

```text
CalculateProposals(...)
PreviewAsync()
```

Delete `CalculateChangesAsync()` after reference check.

---

# 35. Friendlier calculation behavior

In client presentation logic, before calling the generic calculator:

```text
if Item Default
and CurrentPrice is null
and method is not SET_PRICE
and ProposedPrice is blank
```

set:

```text
Status = BLOCKED (backend-compatible)
Warning = "No current Item Default price. Enter the initial selling price in Proposed."
```

Do not change the generic calculator.

Reason:

```text
calculator = arithmetic contract
UI = operator guidance
```

---

# 36. Server Preview remains authoritative

After local calculation, always call existing server `PreviewAsync(...)`.

Server continues to verify:

```text
row identity
baseline
row version
new price
schedule
scope
```

---

# 37. Tests — calculator regression

Existing tests must remain green:

```text
Set_price_allows_null_old_price_and_rounds
Percent_and_amount_methods_block_null_old_price
```

Do not weaken them.

---

# 38. Tests — Item Default service

Add:

### Search includes blank price

Seed:

```text
SellingPrice = null
```

Expected:

```text
row exists
CurrentPrice = null
```

### Effective UOM fallback

Seed:

```text
SellingUom = null
StdUom = "PCS"
```

Expected:

```text
Uom = "PCS"
```

### SellingUom wins

Seed:

```text
SellingUom = "BOX"
StdUom = "PCS"
```

Expected:

```text
Uom = "BOX"
```

### Preview accepts initial price

Selection:

```text
BaselinePrice = null
NewPrice = 150
```

Expected:

```text
Changing / Ready
not blocked
```

### Apply initializes Item Default

Before:

```text
SellingPrice = null
```

After Apply:

```text
SellingPrice = 150
```

Audit:

```text
OldPrice = null
NewPrice = 150
```

### Null baseline concurrency

Review baseline:

```text
null
```

DB changed to:

```text
120
```

Expected:

```text
Apply rejects stale state
120 is not overwritten
```

---

# 39. UI/manual tests

Mandatory:

1. `CALCULATE NEW PRICES` no longer exists.
2. `PREVIEW CHANGES` becomes `REVIEW CHANGES`.
3. no selection → Review disabled.
4. selected priced rows → Review calculates + previews.
5. `SET_PRICE` + blank rows → Proposed auto-populates.
6. relative method + blank rows → `Needs Price`.
7. manually entered Proposed → second Review accepts it.
8. manual Proposed is not overwritten.
9. Apply stays separate.
10. Apply remains disabled until successful Review with no unresolved rows.

---

# 40. Acceptance scenario — mixed items

Target:

```text
Item Default
```

Rows:

```text
A SellingPrice = 100
B SellingPrice = null
```

Method:

```text
Increase %
Value = 10
```

Click only:

```text
REVIEW CHANGES
```

Expected:

```text
A Current 100  Proposed 110  Ready
B Current —    Proposed —    Needs Price
```

Enter:

```text
B Proposed 80
```

Review again:

```text
A 100 → 110 Ready
B —   → 80  Ready
```

Apply:

```text
A SellingPrice = 110
B SellingPrice = 80
```

---

# 41. Acceptance scenario — SET PRICE

Method:

```text
Set Price
Value = 50
```

Select priced and blank Item Default rows.

Review:

```text
all selected rows Proposed = 50
```

Blank rows are not blocked.

---

# 42. Acceptance scenario — import

Import:

```text
Item FG001
Baseline Price blank
New Price 75
```

DB:

```text
SellingPrice = null
```

Expected:

```text
Import valid
Review valid
Apply → SellingPrice = 75
```

---

# 43. No behavior change for Price List / Customer Special

Do not change:

```text
Price List row identity
Price List schedule mode
Price List overlap logic
Price List currency
Customer Special MOQ
Customer Special currency
```

Do not auto-create missing Price List or Customer Special rows.

---

# 44. Build / test gate

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

# 45. Non-regression rejection conditions

Reject if:

- blank Item Default is excluded from search;
- blank Item Default cannot accept manual Proposed Price;
- SET_PRICE still blocks blank Item Default;
- import cannot initialize blank Item Default;
- relative adjustment treats blank as zero;
- manual Proposed is overwritten on Review;
- Item Default UOM remains blank when StdUom exists;
- Calculate + Preview remain as two buttons;
- Review bypasses server Preview;
- Apply occurs without successful Review;
- concurrency safety is weakened;
- Price List / Customer Special behavior changes;
- Sales transaction pricing logic changes;
- a DB migration is added unnecessarily.

---

# 46. Definition of Done

## Blank Item Default
- [ ] blank-price active items load;
- [ ] SET_PRICE works;
- [ ] manual Proposed works;
- [ ] import works;
- [ ] Apply writes `IvStockMaster.SellingPrice`;
- [ ] null-baseline concurrency remains protected.

## Relative methods
- [ ] no null-as-zero calculation;
- [ ] priced rows calculate normally;
- [ ] blank rows show `Needs Price`;
- [ ] warning tells user exactly what to do;
- [ ] typed Proposed survives Review.

## Simplified UX
- [ ] Calculate button removed;
- [ ] Preview renamed to Review;
- [ ] Review calculates + validates in one action;
- [ ] Apply stays separate;
- [ ] selected count remains visible.

## UOM
- [ ] SellingUom wins;
- [ ] StdUom fallback works;
- [ ] no invented UOM;
- [ ] search and current reload agree;
- [ ] audit uses effective UOM.

## Regression
- [ ] calculator tests green;
- [ ] maintenance service tests green;
- [ ] full tests green;
- [ ] build green;
- [ ] no migration;
- [ ] runtime Sales pricing engine unchanged.

---

# 47. FINAL APPROVAL

## **FINAL APPROVED FOR IMPLEMENTATION — 10 / 10**

The finished workflow should be:

```text
Select
→ REVIEW CHANGES
→ enter initial price only where needed
→ REVIEW CHANGES
→ APPLY PRICE CHANGE
```

A missing Item Default price is **not an invalid item**. It is simply an item whose master selling price has not yet been initialized.

The page should help the user initialize it safely without inventing arithmetic such as:

```text
blank + 10%
```

The user supplies the initial price explicitly.

**AI Coding Agent instruction:** simplify the UI workflow and make blank Item Defaults initializable while preserving all existing concurrency, audit, Price List, Customer Special, runtime Sales pricing, posting, tax, discount and costing behavior.
