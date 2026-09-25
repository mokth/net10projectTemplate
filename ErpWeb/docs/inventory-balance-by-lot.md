# Inventory Balance by Lot inquiry

Plan of record: `plans/plan-inventoryBalanceByLot.prompt.md` (Revision 2.2).

## What it is

A read-only, pile-level on-hand inquiry over `dbo.IvBalLoc` (item × warehouse × bin × lot ×
status), with a paged grid, a summary block and an xlsx export. It is deliberately **not** a
picker: it shows more than any of the existing on-hand pickers, and that difference is a decision,
not a bug (see "Divergences from the pickers" below).

## Value column — "Est. value", not a GL valuation

There is no inventory valuation anywhere in this ERP (no report, no service, no GL posting of stock
value). The column is an informational estimate and is labelled as such.

```
unitPrice  = bal.UnitPrice ?? sm.PurchasePrice ?? 0m      // per StdUom
rowValue   = IvQty.Round(StdQty * unitPrice)              // 4 dp, MidpointRounding.AwayFromZero
totalValue = IvQty.Round(SUM(StdQty * unitPrice))         // aggregated in SQL, rounded on the scalar
```

- The rounding sits on the **product**, not the unit price (both price columns are already
  `decimal(18,4)`, so rounding the unit price would be a no-op while the product is the correct
  precision).
- **Known caveat:** `totalValue` is the rounded sum of unrounded products, so it can differ from
  adding the displayed row values by a few `0.0001` when quantities are fractional.
- **Unlike quantity, value does not mix UOMs** — money is summable across `PCS`, `BOX`, `KG`. The
  total still mixes price *vintages* (each pile keeps whatever price it was received at, with no
  revaluation), so it must never be called "inventory value". The quantity KPI is captioned
  **"Total qty (all UOM)"** for the same reason.

## `CanViewPrice` gate (a deliberate, scoped deviation)

Money visibility is gated by a **new `userlogin.CanViewPrice` bit**, published as the
`can_view_price` claim and exposed as `ICurrentUserService.CanViewPrice`. This is a **second**
price-visibility mechanism alongside the menu-based `VIEW_COST` / `VIEW_PRICE` permissions.

- V1 wires it to this page only. Adopting it on any other screen is a separate, separately-approved
  change.
- **Sign-in caveat:** claims are baked once at sign-in (`AuthService.CreatePrincipal`), so a change
  takes effect only after the user signs out and back in. The admin screen carries a hint to that
  effect.
- When `CanViewPrice` is false the service returns `Value = null` and the export **omits the column
  entirely** (a blank column would still disclose that the field exists). The UI merely hides the
  column; it is never the enforcement point.

## "Last movement" semantics of `TransDate`

`IvBalLoc.TransDate` is the date of the **last posted movement**, and it is **mutable** — every
increase/decrease overwrites it with the posted batch's `TrxDtTime`, which can be a back-date, so it
can move backwards. It cannot support history or stock ageing. The grid column is therefore labelled
"Last movement", and the date-range filter means "last movement date within range", not "had a
movement in range".

## Divergences from the pickers (deliberate)

| This inquiry | The pickers (`IvBalLocPicker` / `SearchOnHandPagedAsync`) |
|---|---|
| Includes zero-qty rows by default | `StdQty > 0` hard filter |
| Includes inactive items by default | `sm.IsActive` hard filter |
| Includes non-stock-control items by default | `sm.StockControl` hard filter |
| LEFT-joins `IvStockMaster` → an orphan pile is returned with blank item fields | INNER joins → orphan piles are invisible |

All three inclusion toggles default **true** (show everything, let the user narrow). An orphan pile
is visible whenever `IncludeInactive = true` (the default) and excluded when that toggle is off,
because `sm == null` fails both the inactive and stock-control predicates.

## Filter semantics

- An empty or absent status selection means **all** statuses, including `SCRAPS`.
- Expiry "before" uses the company-local business date; a lot expiring **today is not expired**.
- The last-movement range is half-open: `TransDate >= from.Date && TransDate < to.Date.AddDays(1)`
  — no `.Date` is ever applied to a database column.
- An unknown sort field falls back to the default order (`ICode, WhCode, LocCode, LotNo, Id`).

## Security

- `ACCESS` is checked in the service before any data is read; a denied caller gets `AccessDenied`.
- `EXPORT` is checked inside the export endpoint; a denied caller gets `Forbidden`.
- Company and branch come only from `IInventoryTenantContext.TryBranchScope()` inside the service —
  the export endpoint never accepts them from the query string.
- `CanViewPrice` is evaluated server-side from `ICurrentUserService`.

## DDL / deployment

- `scripts/alter-userlogin-canviewprice.sql` — additive, idempotent, DBA-run. Add it to
  `scripts/init-userlogin.sql` for fresh databases too.
- `scripts/init-inv-balance-lot-menu.sql` — creates the `INV_INQUIRY` parent and the
  `INV_BALANCE_LOT` page + its `ACCESS`/`EXPORT` grants. Ships together with the `menus.xml` rows.

## Phase 0 measurements (TBD — validation only, do not re-tune defaults without the owner)

> To be recorded on the dev `ERPWeb` before first deployment: total `IvBalLoc` rows; rows with
> `StdQty = 0`; rows with non-NULL `UnitPrice`; rows with non-NULL `Cost`; and the count of piles
> with no matching `IvStockMaster` (the orphan piles). These are observability only and do not
> change an approved decision.
