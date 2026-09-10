---
name: DO hide amounts
overview: "UI-only change: hide price, discount, tax/amount displays on Delivery Order entry and list so operators work with qty (and warehouse/ship). Server still calculates and stores commercial fields from item selling price / defaults for later billing."
todos:
  - id: do-entry-hide
    content: "SaDo.razor: hide hero total, grid money cols, tax summary, popup price/discount/tax/preview"
    status: completed
  - id: do-list-hide
    content: "SaDoList: remove TotAmnt column and chip"
    status: completed
isProject: false
---

# Hide DO price / discount / amount (qty-focused UI)

## Decision

**UI visibility only** on DO screens. Do **not** change [`SaDoService`](ErpWeb.Core/Sales/SaDoService.cs) calc/persist of `UnitPrice`, discounts, `GrossAmnt`/`TotAmnt`. Line add still auto-stamps `UnitPrice` from item selling price in code-behind (unchanged); users just cannot see or edit money fields.

Invoice UI stays unchanged.

## Changes

### 1. Entry — [`SaDo.razor`](ErpWeb.UI/Sales/Transactions/SaDo.razor)

**Hero:** remove the `Total incl. tax` KPI block (`TotAmnt`).

**Lines grid:** keep `#`, Item, Warehouse, Qty, Ship. Remove columns:
- `UnitPrice` (Price)
- `Amount`
- `TaxAmt`
- `NetAmount`

**Below grid:** remove the `sinv-tax-summary` block (TOTAL EXCL / TAX / INCL).

**Line popup:** keep Item, Description, Warehouse, Qty, Pack, Remarks. Remove:
- Unit price
- Tax inclusive
- Line tax
- Discount mode + all disc % / disc amount fields
- Amount/Tax/Net preview footer

### 2. List — [`SaDoList.razor`](ErpWeb.UI/Sales/Transactions/SaDoList.razor) + [`.razor.cs`](ErpWeb.UI/Sales/Transactions/SaDoList.razor.cs)

- Remove `TotAmnt` from grid column definitions (`VisibleIndex` resequence if needed).
- Remove the TotAmnt chip in the row card/template if present.

### 3. Code-behind — no logic removal required

[`SaDo.razor.cs`](ErpWeb.UI/Sales/Transactions/SaDo.razor.cs) keeps `RecalcDocument`, discount helpers, and save mapping so stored amounts remain valid. Dead UI-only helpers (`OnPopupDiscountModeChanged`, `PopupDiscountIsAmount`) can stay; unused markup is enough for this slice.

## Acceptance

- DO entry: operators see qty (plus item/warehouse/ship), not price/discount/amount/tax totals.
- DO list: no Total amount column.
- Save/Post still works; DB still has prices/amounts from defaults.
- Invoice screens unchanged.