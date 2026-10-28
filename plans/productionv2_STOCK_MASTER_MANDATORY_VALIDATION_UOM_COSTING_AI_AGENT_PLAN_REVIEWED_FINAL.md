# productionv2 — Stock Master Mandatory Validation, Structural Lock & UOM/Pack-Size Quantity/Costing Safeguard Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified HEAD:** `f620076802a724f7ac193ff4efc341eb35d20752`  
**Plan purpose:** Strict AI Coding Agent implementation plan  
**Review status:** **APPROVED FOR IMPLEMENTATION — 10/10**  
**Priority:** P0 quantity / stock-control / snapshot integrity; P1 master-data UX  
**Review basis:** Current repository code at the verified HEAD, including Inventory, Purchase, Sales, Production, Stock Ledger, and current Stock Master UI/service.

---

# 1. Objective

Strengthen Item/Stock Master validation without rewriting the existing stock/costing engine.

The implementation must guarantee that:

1. A stock-controlled item always has an unambiguous Standard UOM and standard-quantity interpretation.
2. Missing or invalid pack sizes can never silently become `1` for a different UOM.
3. Historical/open document snapshots remain stable when Item Master commercial pack factors are changed later.
4. `StdUom`, `StockControl`, and `LotControl` cannot be changed while any active/open workflow can still create, consume, return, receive, ship, or produce stock using the old structural meaning.
5. Purchase/Sales document chains preserve the correct frozen UOM/pack semantics.
6. FIFO, Moving Average, Standard Cost, stock balances, Production WIP/FG costing, posting, and rollback formulas remain unchanged.
7. Stock Master primary direct UOM factors are consistent with the existing `IvItemUomConversion` infrastructure without silently overwriting unrelated/conflicting Production conversions.
8. Company-level Item Master data is not incorrectly forced to contain branch-owned warehouse/location defaults.

---

# 2. Critical Repository Findings

## 2.1 Stock Master validation is currently weaker than downstream transaction rules

`ErpWeb.Core/Inventory/IvStockMasterService.cs` currently allows these to be blank:

- `IClassCode`
- `StdUom`
- `SellingUom`
- `PurUom`
- `DefWarehouse`
- `DefLocation`
- `StdPackSize`
- `PurStdPackSize`

The service validates many of them only when supplied.

This permits an incomplete stock-controlled master to be saved and pushes failures or wrong quantity interpretation downstream.

## 2.2 Physical stock is stored as Standard Quantity

`IvBalLoc.StdQty` is the physical balance quantity.

The unique stock slice is:

`Company + Branch + Item + Warehouse + Location + Lot + Status`

UOM is not part of the unique stock-slice key.

Therefore `StdUom` is structural: it defines the meaning of every `StdQty` stored for that item.

## 2.3 Current structural lock is correct but too narrow

`IvStockMasterService.EnsureStructuralFieldsUnlockedAsync()` currently blocks changes to:

- `StdUom`
- `StockControl`
- `LotControl`

when the item has:

- any `IvBalLoc`,
- any posted `IvTrxHistory`,
- a NEW non-deleted inventory transaction detail.

This is correct and must remain.

However it is not sufficient.

An item may have no inventory history yet but still have an upstream open document such as a PO, SO, DO, Invoice, Delivery Request, or Work Order that will later create/consume stock under the old structural semantics.

Changing a structural field before that downstream action can corrupt or block the later flow.

## 2.4 Real current example: PO -> GR can change behaviour if StockControl is changed

PO lines freeze:

- `PurchaseUom`
- `StdUom`
- `PackSz`
- purchase quantity / standard quantity

Goods Receipt reads the PO snapshot, but the posting engine later reads the **current** `IvStockMaster.StockControl` to decide whether to create/update an inventory balance slice.

Therefore:

1. Item is StockControl = true.
2. User creates PO.
3. No GR yet, therefore no `IvBalLoc`/history.
4. User changes StockControl = false.
5. GR can later follow the PO stock-item path but inventory posting sees the current item as non-stock.

That structural change must be blocked while the PO remains operational.

## 2.5 Real current example: SO -> DO re-reads current Item Master pack

`SaSoDetail` already freezes:

- `SellingUom`
- `StdUom`
- `StdQty`
- `StdPsize`
- `StockControl`

But current `SaDoService` computes:

- `StdPsize` from current `IvStockMaster.StdPackSize`
- `StdQty` from current pack
- `StockControl` from current Item Master

even when the DO line is linked to an existing SO.

Example:

- SO saved when 1 BOX = 12 PCS
- SO Qty 10 BOX => 120 PCS
- Item Master later changes 1 BOX = 24 PCS
- user creates partial DO for 5 BOX

Current behaviour can calculate:

- 5 × 24 = 120 PCS

instead of the SO snapshot meaning:

- 5 × 12 = 60 PCS

This is a P0 snapshot-integrity defect.

## 2.6 Invoice has the same snapshot problem

Current `SaInvoiceService` recalculates quantity and `StockControl` from current Item Master even when linked to:

- a Delivery Order, or
- a Sales Order.

`SaDoDetail` and `SaSoDetail` already contain frozen structural quantity semantics.

The Invoice must use the upstream frozen snapshot, not re-derive from the current master.

This is especially important because Customer Return and downstream costing can depend on the Invoice line's persisted standard quantity and stock-control flag.

## 2.7 Quotation also has fallback-to-1

`SaQtService` currently does effectively:

`item.StdPackSize > 0 ? item.StdPackSize : 1`

A Quotation freezes `StdPsize`, `StdUom`, `StdQty`, and `StockControl`, and QT -> SO conversion intentionally copies the quotation snapshot.

Therefore a bad master pack factor can be frozen into a quotation and then propagated into SO.

Quotation creation must be hardened too.

## 2.8 Purchase Requisition also has fallback-to-1 paths

`PoPrService` and `PoPrCalc` contain pack-size fallback behaviour.

PR stores:

- `PackSz`
- `StdUom`
- `PurchaseUom`
- standard quantity

PR can later feed PO.

Therefore the fix must cover PR as well as PO.

## 2.9 Goods Receipt correctly uses the PO snapshot

Current GR uses:

`ToStdQty = ReceiveQty × PO.PackSz`

and freezes purchase cost evidence against the derived Standard Quantity.

This is good.

Do not re-read current Item Master pack size at GR time.

The fix is to ensure the PO snapshot was valid when created.

## 2.10 Costing is driven by Standard Quantity

The V2 valuation engine values `FrStdQty` / `ToStdQty`.

This affects:

- Moving Average
- FIFO
- Standard Cost
- sales issue valuation
- return valuation
- Production material costing
- Finished Good receipt valuation

Therefore wrong UOM/pack interpretation is a quantity-and-costing integrity problem even when the total purchase amount initially reconciles.

## 2.11 Production is intentionally strict about UOM conversion

`IvUomConversionService` is the current strict item-specific conversion owner for Production-related conversion paths.

Missing conversion fails closed.

Production Work Orders also snapshot UOM/conversion quantities so later master changes do not rewrite released historical requirements.

Do not replace this mechanism.

## 2.12 `IvItemUomConversion` allows reverse-direction duplicates

The current filtered unique index is directional:

`CompanyCode + ItemCode + FromUom + ToUom`

Therefore both:

- BOX -> PCS
- PCS -> BOX

can exist simultaneously as active rows.

`IvUomConversionService` searches both directions and prefers a forward match.

The Stock Master save must detect a logical duplicate pair and fail instead of silently choosing one.

## 2.13 Default Warehouse is NOT a structural mandatory Item Master field

This is an important correction to the previous plan.

`IvStockMaster` is company-owned.

`IvStockMasterRepository` explicitly queries Item Master by company and comments:

> Company only — never BranchCode on item master.

Warehouses, however, are branch-owned and are validated by:

`Company + Branch + Warehouse`

Therefore forcing one `DefWarehouse` on every stock-controlled company-level item is unsafe in a multi-branch company.

The actual transaction warehouse remains mandatory where stock is moved.

`DefWarehouse` is only a convenience/default and must remain optional.

## 2.14 Default Location is also a convenience/default

Actual transaction location is required when the selected transaction warehouse has active locations.

But Item Master `DefLocation` is only a default.

An item may validly have:

- no default warehouse/location,
- a default warehouse but no default bin because the operator selects the bin per transaction.

Therefore do not make `DefLocation` mandatory merely because the selected default warehouse has locations.

Validate it only when supplied.

---

# 3. Correct Mandatory Rules

## 3.1 Preserve existing always-required fields

Keep the current rules:

1. `ICode`
2. `IDesc`
3. `Classification`
4. `SellingGlCode`

Preserve:

- Add/Edit permission checks
- duplicate Item Code check
- RowVersion concurrency
- lot/stock relationship
- expiry policy
- Min Stock <= Max Stock
- existing active-master lookup validation

## 3.2 Additional requirements for `StockControl == true`

Require:

1. `IClassCode`
   - not blank
   - active
   - valid for company

2. `StdUom`
   - not blank
   - active
   - valid for company

3. `SellingUom`
   - normalize blank to `StdUom`
   - active
   - valid

4. `PurUom`
   - normalize blank to `StdUom`
   - active
   - valid

5. `StdPackSize`
   - if `SellingUom == StdUom`: normalize to exactly `1`
   - otherwise explicitly `> 0`

6. `PurStdPackSize`
   - if `PurUom == StdUom`: normalize to exactly `1`
   - otherwise explicitly `> 0`

7. `StockControl`
   - existing explicit boolean semantics

8. `LotControl`
   - existing explicit boolean semantics
   - if true, StockControl must be true

9. `ExpiryControl`
   - existing enum/policy
   - must be `NONE` if LotControl is false

## 3.3 Optional default fields

`DefWarehouse`:

- optional
- if supplied: must be active in the current branch
- never make stock posting depend on it

`DefLocation`:

- optional
- if supplied:
  - `DefWarehouse` must be supplied
  - location must be active under that warehouse
- do not require a default location merely because the warehouse has active bins

Actual transaction validation remains responsible for requiring an actual warehouse/location when stock is moved.

## 3.4 Non-stock item

For `StockControl == false`:

- do not impose stock-only warehouse/class/pack requirements unless an existing downstream non-stock path already requires them
- LotControl must remain false
- ExpiryControl must remain NONE when LotControl is false

Do not redesign non-stock/service item semantics in this plan.

---

# 4. UOM / Pack Factor Contract

## 4.1 Standard UOM

`StdUom` is the UOM in which `IvBalLoc.StdQty` is interpreted.

It is structural.

After structural usage exists, it cannot be directly changed.

## 4.2 Selling factor

Interpret:

`1 SellingUom = StdPackSize × StdUom`

Examples:

- PCS -> PCS: factor = 1
- BOX -> PCS: factor = 12 means 1 BOX = 12 PCS

Rules:

- same UOM => exactly 1
- different UOM => explicit positive factor
- null/0/negative is invalid
- never silently assume 1 for a different UOM

## 4.3 Purchase factor

Interpret:

`1 PurUom = PurStdPackSize × StdUom`

Rules are identical:

- same UOM => exactly 1
- different UOM => explicit positive factor
- no null/0/negative fallback

## 4.4 Same Selling and Purchase UOM

If:

`SellingUom == PurUom`

then:

`StdPackSize == PurStdPackSize`

must hold.

The same item/UOM pair cannot mean two different physical quantities.

---

# 5. Structural Field Lock — Expand the Current Guard

Structural fields:

- `StdUom`
- `StockControl`
- `LotControl`

The current inventory guard remains the first layer.

Add an operational dependency guard.

## 5.1 Existing inventory footprint — keep current block

Block if any of these exist:

- `IvBalLoc`
- `IvTrxHistory`
- NEW/non-deleted Inventory transaction detail

This includes zero-balance `IvBalLoc`.

Do not weaken this.

## 5.2 Open Purchase dependencies

Block structural changes when the item participates in an operational Purchase flow that can still become stock.

At minimum cover:

- active/current PR detail that is not fully consumed/cancelled
- latest operational PO revision with remaining quantity
- PO lines that can still be GR-picked/reopened into GR-pickable state

Do not let superseded historical PO revisions lock the item forever.

Use the existing PO revision/status policy instead of inventing status strings.

## 5.3 Open Sales dependencies

Block structural changes while current/open lines can still flow to shipment/return semantics.

At minimum cover:

- current quotation with remaining convertible quantity
- current/non-closed Sales Order with remaining fulfilment
- NEW/POSTED/operational Delivery Order line where stock semantics are still relevant
- NEW/POSTED/operational Invoice line where stock semantics are still relevant
- active Delivery Request source for the item

Reason:

The Sales chain persists structural snapshots. Changing the Item Master structural meaning while open snapshots exist can create a mixed semantic chain.

## 5.4 Production dependencies

Block structural changes while the item participates in non-terminal Production execution.

At minimum cover:

- non-cancelled/non-terminal Work Order where item is `ProductCode`
- non-terminal Work Order material where item is `ComponentCode`
- active Production balance (`ProductionBalLot`) with positive quantity for the item
- active FG staging that still requires inventory receipt

Do not lock forever merely because a historical completed Work Order exists with no remaining Production balance and no inventory footprint.

## 5.5 Save-time authority

Even if UI disables structural controls, `SaveAsync()` must re-run the guard.

This handles races where another user creates a PO/SO/WO after the Stock Master page was opened.

## 5.6 UI lock state

Extend `IvStockMasterEditVm` or add a small read model so edit mode can show:

- `StructuralFieldsLocked`
- `StructuralLockReason`

When locked:

- disable `StdUom`
- disable `StockControl`
- disable `LotControl`
- show a clear inline explanation

Example:

`Stock Control is locked because this item has open Purchase/Sales/Production activity or inventory history.`

Do not rely on UI locking for correctness.

---

# 6. Primary UOM Conversion Reconciliation

Reuse:

- `IvItemUomConversion`
- `IvUomConversionService`

Do not create another conversion table/service.

## 6.1 Logical pair lookup

For each non-identity primary pair:

- SellingUom <-> StdUom
- PurUom <-> StdUom

load active rows in **both directions**.

## 6.2 Duplicate logical pair

If more than one active row exists across both directions:

- fail Stock Master save
- do not guess
- do not auto-delete one row

Message should identify the pair.

Example:

`Item ABC has multiple active BOX/PCS conversion rows. Resolve the conversion conflict before saving.`

## 6.3 Missing pair

If no active logical row exists:

create canonical:

- FromUom = source UOM
- ToUom = StdUom
- FromQty = 1
- ToQty = pack factor
- IsActive = true
- normal default rounding settings

This is safe and user-friendly.

## 6.4 Existing matching pair

If exactly one active logical row exists and its effective factor equals the requested Stock Master factor within the repository's quantity precision:

- accept it
- do not rewrite it unnecessarily

## 6.5 Existing conflicting pair — DO NOT blindly overwrite

This corrects the previous plan.

Never overwrite a conflicting approved conversion merely because Stock Master was saved.

Use this rule on edit:

### Case A — existing conversion matches the item's OLD saved pack factor

If:

- old Stock Master pair/factor was valid, and
- the active conversion represented that old factor, and
- the user deliberately changes that same primary pack factor,

then Stock Master may update that one conversion row atomically to the new factor.

Preserve:

- row identity
- rounding scale
- rounding mode
- audit fields
- RowVersion concurrency

This is an explicit primary-factor change from a previously consistent state.

### Case B — existing conversion did NOT match the OLD saved pack factor

Fail.

Do not "repair" by guessing which source is correct.

Message example:

`Purchase pack size conflicts with the existing approved CTN/PCS conversion. No data was changed.`

## 6.6 When the UOM code changes

If Selling/Purchase UOM changes to a new UOM:

- create/reconcile the new logical pair
- do NOT automatically deactivate the old conversion pair

The old pair may still be required by:

- Product Definitions
- Work Order snapshots
- other valid item UOM uses

Removing a conversion is outside this plan.

## 6.7 Identity pair

If source UOM == StdUom:

- factor is exactly 1
- no conversion row required

## 6.8 Atomicity

Item changes and primary conversion changes must be part of the same database unit of work.

All conversion-conflict validation should happen **before** external image-file persistence where practical.

Do not save the Item Master and then fail conversion synchronization afterward.

## 6.9 Conversion concurrency

If an `IvItemUomConversion.RowVersion` conflict occurs:

- return concurrency failure
- clearly identify that the Item/UOM conversion changed
- do not report it as a duplicate Item Code

---

# 7. Sales Snapshot Integrity

This is P0.

## 7.1 Manual/new document lines

For manually entered lines with no upstream document snapshot:

- use current Item Master
- require valid stock-item Standard/Selling UOM
- require valid positive selling factor
- no fallback to 1 for different UOM

Apply to:

- Quotation
- Sales Order
- Delivery Order
- Invoice

## 7.2 Quotation

File:

`ErpWeb.Core/Sales/SaQtService.cs`

Remove stock-item fallback:

`StdPackSize missing/<=0 => 1`

For stock-controlled items:

- same Selling/Std UOM => factor 1
- different UOM => valid positive factor required

Persist the valid frozen snapshot as today:

- `SellingUom`
- `StdUom`
- `StdPsize`
- `StdQty`
- `StockControl`

QT revision behaviour remains unchanged.

## 7.3 QT -> SO

Current design intentionally copies the quotation snapshot.

Keep that.

Do not re-derive QT-converted SO standard quantity from current Item Master.

Add validation that the frozen QT snapshot itself is structurally valid.

If a legacy quotation contains an invalid/missing stock snapshot:

- fail conversion clearly
- do not invent factor 1

## 7.4 Manual SO

File:

`ErpWeb.Core/Sales/SaSoService.cs`

For stock-controlled item:

- require valid current Standard/Selling UOM
- require positive factor
- persist snapshot

No fallback 1 for different UOM.

## 7.5 SO -> DO

File:

`ErpWeb.Core/Sales/SaDoService.cs`

For a DO line linked to an SO:

**use the SO line snapshot as authority for stock quantity semantics.**

Use:

- `SaSoDetail.SellingUom`
- `SaSoDetail.StdUom`
- `SaSoDetail.StdPsize`
- `SaSoDetail.StockControl`

Calculate partial DO quantity using:

`DO StdQty = DO Qty × SO.StdPsize`

Do not use current Item Master `StdPackSize` for the linked line.

The current item may still be read for:

- existence/active checks,
- non-structural display/defaults where appropriate,

but must not rewrite the frozen SO stock semantics.

## 7.6 DO -> Invoice

File:

`ErpWeb.Core/Sales/SaInvoiceService.cs`

For `LinkDo`:

use `SaDoDetail` snapshot as authority:

- `StdPsize`
- `StdUom`
- `SellingUom`
- `StockControl`

Calculate partial invoice Standard Qty using the DO's frozen factor.

Do not re-read current Item Master factor/StockControl for structural quantity semantics.

## 7.7 SO -> direct Invoice

When Invoice is directly linked to SO:

use `SaSoDetail` snapshot as authority.

Do not recalculate using current Item Master factor/StockControl.

## 7.8 Manual/direct Invoice without upstream SO/DO

Use current Item Master, with strict pack validation and no invalid fallback.

## 7.9 Legacy upstream document with invalid snapshot

Fail closed.

Examples:

- `StdPsize <= 0`
- missing `StdUom` for stock-controlled snapshot
- contradictory upstream stock data

Do not silently replace the frozen value with current Item Master.

That would rewrite document meaning.

---

# 8. Purchase Snapshot Integrity

## 8.1 Purchase Requisition

Files:

- `ErpWeb.Core/Purchase/PoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrCalc.cs`

For stock-master items:

- Purchase UOM required after default/normalization
- Std UOM required
- purchase-to-standard factor valid and positive
- no factor-1 repair for a different UOM

Work Order shortage -> PR path must also reject invalid pack configuration rather than use `EffectivePackSize(0) = 1`.

For one-time/indirect Purchase items:

- preserve existing semantics
- do not impose stock-master-only rules

## 8.2 PR -> PO

If PO line is sourced from PR:

prefer the valid PR snapshot.

Do not replace a valid frozen PR factor with a later changed Item Master factor.

Existing line-level constraints that prevent changing consumed/linked PR semantics must remain.

## 8.3 Manual/new PO

Files:

- `ErpWeb.Core/Purchase/PoOrderService.cs`
- `ErpWeb.Core/Purchase/PoPurchasingItemLookupService.cs`
- `ErpWeb.Core/Purchase/PoOrderCalc.cs`

For stock-master items:

- valid Purchase UOM
- valid Std UOM
- valid positive factor
- no `?? 1m` / zero-to-1 repair for a different UOM

Preserve one-time/indirect item behaviour.

## 8.4 PO -> GR

Keep current snapshot behaviour.

GR must continue to use:

- PO Purchase UOM
- PO Std UOM
- PO PackSz

Do not re-derive pack factor from current Item Master.

Add regression tests to prove this remains true after Item Master pack size changes.

---

# 9. Default Warehouse / Location Behaviour

This section replaces the old mandatory-default rule.

## 9.1 Item Master

`DefWarehouse` is optional.

If blank:

- item save is valid
- actual stock transaction still requires/chooses an operational warehouse

If supplied:

- validate active warehouse in current company/branch

## 9.2 Default Location

`DefLocation` is optional.

If supplied:

- DefWarehouse must exist
- location must be active under selected warehouse

Do not require a default location simply because the warehouse has locations.

## 9.3 Transaction layer remains authoritative

Preserve existing transaction behaviour:

- actual warehouse required when stock movement requires it
- if actual warehouse has active locations, actual transaction location is required
- transaction-level branch validation remains unchanged

Do not move this responsibility into Item Master.

---

# 10. Stock Master Server Implementation

Primary:

`ErpWeb.Core/Inventory/IvStockMasterService.cs`

Supporting model:

`ErpWeb.Core/Inventory/IvMasterResults.cs`

## 10.1 Authoritative validation flow

Recommended order:

1. Basic current validation.
2. Normalize Item Code / description.
3. Normalize/validate Item Class.
4. Normalize/validate Standard UOM.
5. Default/validate Selling UOM.
6. Default/validate Purchase UOM.
7. Normalize/validate pack factors.
8. Validate optional default warehouse/location.
9. Load existing item on edit.
10. Run structural dependency guard if structural field changed.
11. Validate/reconcile primary UOM conversions.
12. Validate gallery mutation.
13. Store external image additions.
14. Apply item/conversion DB changes.
15. `SaveChangesAsync`.
16. Reload and return.

Do not persist image files before a known conversion/structural conflict when it can be detected first.

## 10.2 Small reusable pack policy

To avoid Sales/Purchase duplicating the same rules, add a small pure Core helper if appropriate, e.g.:

`IvItemPackPolicy`

Responsibilities only:

- normalize same-UOM factor to 1
- validate source/standard UOM
- validate positive different-UOM factor
- compare factors with repository quantity precision

Do not put DB access, posting, costing, or document status logic in this helper.

## 10.3 Structural guard helper

Keep one authoritative helper in Stock Master service/Core.

It should return:

- locked/unlocked
- concise reason
- optional reference category

Do not duplicate different lock rules in UI.

---

# 11. Stock Master UI

Files:

- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

## 11.1 Required indicators when StockControl = true

Clearly mark:

- Item Class
- Standard UOM
- Selling UOM
- Selling/standard pack factor
- Purchase UOM
- Purchase/standard pack factor

Do **not** mark Default Warehouse/Location required.

## 11.2 Friendly same-UOM defaults

When Std UOM is selected:

if Selling UOM blank:

`SellingUom = StdUom`

if Purchase UOM blank:

`PurUom = StdUom`

if Selling UOM == Std UOM:

`StdPackSize = 1`

if Purchase UOM == Std UOM:

`PurStdPackSize = 1`

Do not overwrite a user's different UOM/factor unnecessarily.

## 11.3 Factor UX

Instead of only "Std pack size", show meaning:

`1 [Selling UOM] = [factor] [Std UOM]`

Example:

`1 BOX = [12] PCS`

Purchase:

`1 CTN = [48] PCS`

Same-UOM factor should display 1 and be disabled/read-only.

## 11.4 Structural lock UX

When locked:

- disable Stock Control
- disable Standard UOM
- disable Lot Control
- show reason

The server still rechecks on Save.

## 11.5 StockControl checkbox event

Replace plain blind binding if needed with a controlled handler so UI can:

- clear LotControl/Expiry when turning StockControl off on an unused item
- restore no hidden invalid state
- show lock reason when change is forbidden

Do not use UI event handling as the only protection.

## 11.6 Warehouse/location

Keep existing lookup controls.

Add hint text:

- Default Warehouse: optional transaction default
- Default Location: optional bin default

Actual transaction requirements remain downstream.

---

# 12. Existing Data Policy

No schema migration and no silent mass update.

Existing incomplete items:

- can be viewed
- when edited/saved, must pass the new validation
- downstream transaction creation must fail safely if legacy UOM/pack data is invalid

Do not auto-fill historical database rows globally.

Do not recalculate historical transactions.

---

# 13. Database / Schema Impact

Expected schema change:

**None.**

Reuse:

- `IvStockMaster`
- `IvItemUomConversion`
- existing RowVersion fields
- existing indexes

Do not make nullable Item Master columns `NOT NULL` in this task.

Reason:

- legacy data may exist
- warehouse defaults are intentionally optional
- service/domain validation is the correct compatibility gate

If implementation discovers an unavoidable schema requirement, stop and re-review before adding migration.

---

# 14. Costing / Balance Invariants — MUST PRESERVE

Do not change:

1. `IvBalLoc.StdQty` precision/meaning.
2. `IvStockSliceKey`.
3. negative-stock prevention.
4. branch posting locks.
5. posting transaction boundaries.
6. duplicate-post protection.
7. StockPosting sequence/idempotency.
8. FIFO layer allocation.
9. Moving Average state calculation.
10. Standard Cost effective revision resolution.
11. GR purchase cost evidence.
12. exact reversal/rollback facts.
13. period-close guards.
14. future-date/future-stock guards.
15. Inventory history snapshots.
16. Production WIP costing.
17. Production FG costing.
18. Sales return original-cost allocation.
19. Purchase return valuation.
20. transaction quantity rounding rules.

---

# 15. MUST NOT DO

The AI coding agent must not:

1. Rewrite FIFO formulas.
2. Rewrite Moving Average formulas.
3. Rewrite Standard Cost formulas.
4. Make Item Master PurchasePrice a valuation authority.
5. Make Item Master SellingPrice a valuation authority.
6. Recalculate existing IvTrxHistory from new pack factors.
7. Recalculate existing StockValuationFact.
8. Rewrite FIFO layers during Item Master save.
9. Update `IvBalLoc.StdQty` during Item Master save.
10. Change stock-slice identity.
11. Weaken structural lock.
12. Allow StockControl to change merely because current on-hand is zero.
13. Auto-delete old UOM conversions when commercial UOM changes.
14. Blindly overwrite a conflicting approved UOM conversion.
15. Create a second UOM conversion table/service.
16. Change closed-period rules.
17. Change future-date rules.
18. Change reservation rules.
19. Change Sales/Purchase status machines.
20. Change Production Work Order snapshot architecture.
21. Force company-level Item Master to have a branch-specific warehouse default.
22. Make current Item Master pack size rewrite an upstream document snapshot.
23. Use fallback factor 1 for different UOMs.
24. "Fix" a legacy invalid upstream document by silently substituting current Item Master values.

---

# 16. Implementation Phases

## Phase 1 — Core pack/UOM validation

Implement:

- Item Class requirement for stock-controlled items
- Standard UOM requirement
- Selling/Purchase UOM defaults
- same-UOM factor=1
- different-UOM positive-factor requirement
- same pair factor consistency
- optional default warehouse/location validation

Gate:

**An invalid stock-controlled item cannot be saved through the service.**

## Phase 2 — Structural dependency lock

Expand `EnsureStructuralFieldsUnlockedAsync()` or replace it with a stronger equivalent.

Cover:

- current Inventory footprint
- open Purchase
- open Sales
- active Delivery Request
- non-terminal Production / active Production balance

Gate:

**StdUom / StockControl / LotControl cannot change while any live workflow can still depend on the old meaning.**

## Phase 3 — UOM conversion reconciliation

Implement the safe reconciliation rules in §6.

Gate:

**Stock Master primary factor and active logical conversion cannot disagree after a successful save. No conflicting conversion is silently overwritten.**

## Phase 4 — Stock Master UX

Implement:

- required marks
- friendly defaults
- factor equation UI
- structural lock display
- optional warehouse/location hints

Gate:

**Common PCS/PCS setup requires minimal typing and structural restrictions are understandable before Save.**

## Phase 5 — Purchase hardening

Cover:

- PR
- Work Order shortage -> PR
- PR -> PO
- manual PO
- purchasing item lookup
- PO -> GR snapshot regression

Gate:

**No stock Purchase flow can persist an invalid standard quantity because of null/zero pack fallback.**

## Phase 6 — Sales hardening

Cover:

- QT
- QT -> SO
- manual SO
- SO -> DO
- DO -> Invoice
- SO -> direct Invoice
- manual Invoice

Gate:

**Upstream snapshots remain authoritative and later Item Master pack changes cannot alter the quantity meaning of existing documents.**

## Phase 7 — Inventory / costing / Production regression

Run focused end-to-end regression.

Gate:

**No change to stock quantity, costing, WIP, posting, rollback, or concurrency behaviour except rejection of invalid master/snapshot data.**

---

# 17. Required Tests

## 17.1 Stock Master validation tests

Add to:

`ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs`

Test:

1. Stock item blank Item Class -> fail.
2. Inactive Item Class -> fail.
3. Blank Std UOM -> fail.
4. Inactive Std UOM -> fail.
5. Blank Selling UOM -> default Std UOM.
6. Blank Purchase UOM -> default Std UOM.
7. Selling == Std -> factor normalized to 1.
8. Purchase == Std -> factor normalized to 1.
9. Different Selling UOM + null factor -> fail.
10. Different Selling UOM + 0 factor -> fail.
11. Different Selling UOM + negative factor -> fail.
12. Different Purchase UOM + null/0/negative -> fail.
13. Same Selling/Purchase UOM + differing factors -> fail.
14. Blank DefWarehouse -> pass.
15. DefWarehouse supplied but inactive/other branch -> fail.
16. Blank DefLocation -> pass.
17. DefLocation without warehouse -> fail.
18. Invalid location under warehouse -> fail.
19. Non-stock item retains intended flexibility.
20. permissions unchanged.
21. RowVersion unchanged.

## 17.2 Structural lock tests

Test every structural field:

- StdUom
- StockControl
- LotControl

against:

1. IvBalLoc exists, even qty 0 -> blocked.
2. IvTrxHistory exists -> blocked.
3. NEW Inventory draft exists -> blocked.
4. open PR -> blocked.
5. operational latest PO with remaining qty -> blocked.
6. current unconverted QT with remaining qty -> blocked.
7. current open SO -> blocked.
8. operational DO -> blocked.
9. operational Invoice -> blocked.
10. active Delivery Request source -> blocked.
11. non-terminal WO product -> blocked.
12. non-terminal WO material -> blocked.
13. positive ProductionBalLot -> blocked.
14. superseded/fully terminal historical document with no live dependency and no stock footprint -> does not falsely lock.

Add concurrency test:

- page loads unlocked
- another process creates open dependency
- Save recheck blocks structural change

## 17.3 Conversion reconciliation tests

Test:

1. new BOX->PCS pair creates canonical row.
2. reverse existing pair with same effective factor accepted.
3. forward existing pair with same factor accepted.
4. both forward and reverse active -> fail ambiguous.
5. existing pair conflicts with OLD master -> fail; no overwrite.
6. existing pair matches OLD master, user changes pack -> row updates atomically.
7. update preserves rounding mode/scale.
8. identity UOM creates no row.
9. Selling and Purchase same logical pair -> one reconciliation.
10. changed Selling UOM creates new pair and does not delete old pair.
11. conversion RowVersion conflict -> concurrency failure.
12. item + conversion change is atomic.

## 17.4 Quotation tests

Add/extend:

`ErpWeb.Tests/Sales/Transaction/SaQtServiceTests.cs`

Test:

1. different UOM + missing pack -> fail.
2. BOX=12 Qty=2 -> StdQty 24.
3. snapshot persists StdPsize=12.
4. no silent fallback 1.

## 17.5 QT -> SO snapshot test

Create quotation BOX=12.

Then change current Item Master pack to 24.

Convert QT -> SO.

Assert SO retains:

- StdPsize=12
- correct StdQty
- original StdUom/SellingUom
- original StockControl

Do not recalc from current master.

## 17.6 SO tests

Add/extend:

`ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs`

Manual SO:

- invalid master factor rejected
- valid BOX=12 -> correct StdQty

## 17.7 SO -> DO snapshot test

Add/extend:

`ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`

Scenario:

1. SO BOX=12.
2. Change Item Master StdPackSize to 24.
3. Create DO linked to SO for 5 BOX.

Assert:

- DO StdPsize = 12
- DO StdQty = 60
- DO StdUom = SO snapshot StdUom
- DO StockControl = SO snapshot StockControl

Do not use current master factor 24.

## 17.8 Invoice snapshot tests

Add/extend:

`ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs`

### DO -> Invoice

1. DO snapshot factor 12.
2. Item Master now factor 24.
3. Invoice linked to DO.

Assert invoice Standard Quantity follows DO factor 12.

### SO -> direct Invoice

Same assertion using SO snapshot.

### Manual invoice

Uses current valid master factor.

Legacy invalid upstream snapshot:

- fail clearly
- no master fallback

## 17.9 PR tests

Add/extend existing Procurement PR tests.

Test:

1. stock item with invalid purchase factor rejected.
2. Work Order shortage auto-PR does not use 1 for invalid factor.
3. valid CTN=48 creates correct standard quantity.
4. PR snapshot remains stable after master factor changes.

## 17.10 PO tests

`ErpWeb.Tests/Procurement/Transaction/PoOrderServiceTests.cs`

Test:

1. manual stock PO invalid pack -> fail.
2. valid CTN=48 -> correct `PoQty`.
3. PR-linked PO preserves PR snapshot.
4. indirect/one-time behaviour unchanged.

## 17.11 GR tests

`ErpWeb.Tests/Inventory/Transaction/IvGoodsReceiptServiceTests.cs`

Scenario:

- PO created at CTN=48
- later master Purchase Pack changed to 24
- receive 10 CTN

Assert GR still uses frozen PO:

- ToPurQty = 10
- ToPurUom = CTN
- ToStdQty = 480
- ToStdUom = PCS

Do not use current master pack 24.

## 17.12 Inventory posting / rollback

`ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs`

Post above GR:

- balance +480 PCS
- history +480 PCS
- correct stock slice
- no duplicate slice

Rollback:

- exact -480
- prior balance restored
- V2 reversal/history correct
- no negative balance
- valuation reversal exact

## 17.13 Costing regression

For Moving Average:

- receipt value correct
- unit cost correct
- issue value correct
- exact reversal

For FIFO:

- layer receipt qty correct
- issue consumes correct Standard Qty
- layer value correct
- reversal restores exact layer state/value

For Standard Cost:

- effective Standard Cost revision remains authority
- quantity remains Standard Qty
- Item Master price never substitutes

## 17.14 Production regression

Test:

1. primary conversion is resolvable through `IvUomConversionService`.
2. missing unrelated Production conversion still fails closed.
3. Work Order snapshot quantities are unchanged by later pack edits.
4. structural StockControl change is blocked while non-terminal WO depends on item.
5. FG staging item cannot be structurally changed out from under an unreceived FG balance.
6. Production material issue remains based on frozen `BaseUom` / conversion factor.
7. no Production costing formula changed.

## 17.15 Multi-branch default test

Company has Branch A and Branch B.

Item has no default warehouse.

Assert:

- Item Master save succeeds.
- Branch A transaction can select A warehouse.
- Branch B transaction can select B warehouse.
- stock balances stay branch-specific.

If DefWarehouse is supplied in Branch A:

- validate against Branch A only
- do not treat it as a company-wide mandatory structural identity.

---

# 18. Key End-to-End Examples

## Example A — Purchase

Item:

- Std UOM = PCS
- Purchase UOM = CTN
- Purchase pack = 48

PO:

- 10 CTN
- StdQty = 480 PCS

Later Item Master changes purchase pack to 24.

GR against the old PO:

**must still receive 480 PCS**, because PO is the frozen purchasing snapshot.

If PO net value = RM960:

- inventory qty = 480 PCS
- total value = RM960
- base unit cost = RM2/PCS

Rollback restores exact prior qty/value.

## Example B — Sales

SO created:

- Selling UOM = BOX
- Std UOM = PCS
- factor = 12
- Qty = 10 BOX
- StdQty = 120 PCS

Later Item Master factor changes to 24.

DO against SO:

- Qty = 5 BOX

Expected:

- StdQty = 60 PCS

Not 120 PCS.

Invoice linked to that DO must retain the same frozen quantity semantics.

## Example C — StockControl change

Item:

- StockControl = true
- no current inventory yet
- open PO exists

User attempts:

`StockControl = false`

Expected:

**BLOCK**

Reason:

The open PO can still become a stock receipt under the old structural semantics.

## Example D — Zero on-hand

Item previously had stock but now `IvBalLoc.StdQty = 0`.

User attempts StockControl=false.

Expected:

**BLOCK**

Reason:

The item already has inventory structure/history and its historical stock meaning must remain stable.

---

# 19. Files Expected to Change

## Inventory / master

- `ErpWeb.Core/Inventory/IvStockMasterService.cs`
- `ErpWeb.Core/Inventory/IvMasterResults.cs`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

Possible small helper:

- `ErpWeb.Core/Inventory/IvItemPackPolicy.cs`

Do not introduce a large new subsystem.

## Purchase

- `ErpWeb.Core/Purchase/PoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrCalc.cs` if helper fallback must be restricted
- `ErpWeb.Core/Purchase/PoOrderService.cs`
- `ErpWeb.Core/Purchase/PoOrderCalc.cs` if helper fallback must be restricted
- `ErpWeb.Core/Purchase/PoPurchasingItemLookupService.cs`

## Sales

- `ErpWeb.Core/Sales/SaQtService.cs`
- `ErpWeb.Core/Sales/SaSoService.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`

## Tests

- `ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvGoodsReceiptServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs`
- relevant Production Work Order/material/FG tests
- relevant PR tests
- `ErpWeb.Tests/Procurement/Transaction/PoOrderServiceTests.cs`
- `ErpWeb.Tests/Procurement/Transaction/PoOrderCalcTests.cs` only if helper semantics change
- `ErpWeb.Tests/Sales/Transaction/SaQtServiceTests.cs`
- `ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`
- `ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs`

---

# 20. Implementation Gate Checklist

Before completing each phase, the AI agent must report:

### Gate A — Master
- required stock fields enforced
- warehouse/location remain optional defaults
- no invalid factor fallback

### Gate B — Structural
- Inventory references checked
- Purchase references checked
- Sales references checked
- Production references checked
- Save-time race recheck exists

### Gate C — Conversion
- no logical reverse duplicate ambiguity
- missing direct primary pair can be created
- conflicting existing conversion not blindly overwritten
- atomic save
- concurrency covered

### Gate D — Purchase
- PR hardened
- PO hardened
- GR uses frozen PO factor

### Gate E — Sales
- QT hardened
- SO hardened
- SO->DO uses SO snapshot
- DO->Invoice uses DO snapshot
- SO->Invoice uses SO snapshot
- manual document paths use current valid master only

### Gate F — Costing
- inventory qty exact
- WAC exact
- FIFO exact
- Standard Cost unchanged
- rollback exact

### Gate G — Production
- Work Order snapshots unchanged
- material issue unchanged
- FG costing unchanged
- active dependencies prevent structural mutation

Do not proceed to the next phase when a gate fails.

---

# 21. Acceptance Criteria

Implementation is accepted only when:

1. Stock-controlled item cannot save without valid Item Class.
2. Stock-controlled item cannot save without Standard UOM.
3. Selling/Purchase UOM default safely to Standard UOM when blank.
4. Same-UOM factor is exactly 1.
5. Different-UOM factor cannot be null/zero/negative.
6. Default Warehouse remains optional.
7. Default Location remains optional.
8. Supplied warehouse/location is validated.
9. Structural fields lock after inventory footprint.
10. Structural fields also lock against live Purchase/Sales/Production dependencies.
11. Open PO prevents StockControl/StdUom/LotControl change.
12. Open SO/DO/Invoice/DR prevents unsafe structural change.
13. Non-terminal Production dependency prevents unsafe structural change.
14. Reverse-direction duplicate UOM conversions are detected.
15. A conflicting pre-existing UOM conversion is never silently overwritten.
16. Valid deliberate primary factor change can update a previously consistent conversion atomically.
17. Quotation no longer silently uses factor 1 for invalid stock item.
18. PR no longer silently uses factor 1 for invalid stock item.
19. PO no longer silently uses factor 1 for invalid stock item.
20. SO no longer silently uses factor 1 for invalid stock item.
21. Manual DO/Invoice no longer silently uses factor 1 for invalid stock item.
22. QT->SO preserves QT snapshot.
23. SO->DO preserves SO snapshot.
24. DO->Invoice preserves DO snapshot.
25. SO->Invoice preserves SO snapshot.
26. PO->GR preserves PO snapshot.
27. Item Master pack changes do not rewrite existing document quantity meaning.
28. 10 CTN at 48 posts as 480 PCS.
29. 2 BOX at 12 posts/snapshots as 24 PCS.
30. FIFO/WAC/Standard Cost remain based on correct Standard Qty.
31. rollback restores exact prior quantity/value.
32. no historical posting is recalculated.
33. no Production WIP/FG costing formula changes.
34. no stock ledger architecture changes.
35. permissions/concurrency remain intact.
36. common same-UOM Stock Master entry remains simple and user-friendly.

---

# 22. Review Result

The previous plan was **not approved unchanged** because it had four material design gaps:

1. It made branch-owned warehouse/location defaults mandatory on a company-owned Item Master.
2. It did not extend structural locking to open upstream Purchase/Sales/Production workflows.
3. It hardened only part of the fallback-to-1 surface and missed Quotation, PR, and Invoice.
4. It did not protect the frozen SO/DO/Invoice quantity chain from later Item Master pack changes.
5. It allowed a Stock Master save to overwrite a conflicting existing `IvItemUomConversion` too aggressively.

This revised plan corrects those issues while preserving the current posting/costing architecture.

**FINAL STATUS: APPROVED FOR IMPLEMENTATION — 10/10**

The implementation principle is:

> **Current master data defines new transactions; persisted upstream snapshots define downstream transactions; structural stock identity becomes immutable while any live or historical inventory dependency exists.**

Do not solve master-data inconsistency by rewriting costing, stock balances, or historical document snapshots.
