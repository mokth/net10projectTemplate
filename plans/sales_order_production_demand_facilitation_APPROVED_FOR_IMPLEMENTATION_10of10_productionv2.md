# Sales Order Production-Demand Facilitation
## APPROVED FOR IMPLEMENTATION — 10/10 — `productionv2`

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified current HEAD:** `1b806666b9518bff6760239cf7b31b9d10530c81`  
**Baseline commit:** `check in dr again`  
**Review status:** **APPROVED FOR IMPLEMENTATION**  
**Plan score:** **10/10 — repo-grounded execution specification**

> This revision supersedes the previous plan. Approval is tied to the verified HEAD above.  
> If any named implementation file changes before coding starts, the Code Agent must re-diff that file and preserve the contracts in this plan.

---

# 1. Objective

Make Sales Order entry production-aware while keeping normal Sales entry simple:

> **Customer → Item → Quantity → Warehouse → Required Delivery Date → Save**

Ordinary Sales users must **not** decide:

- BUY / MAKE / PHANTOM;
- Need DR = Yes/No;
- Product Definition revision;
- Work Order;
- MRP.

The ERP must derive whether SO demand belongs in production planning from controlled master and production setup.

Approved high-level flow:

```text
Item / Product Definition
        ↓
system-owned supply method
        ↓
Sales Order customer demand
        ↓
production-demand eligibility
        ↓
Delivery Request planning / consolidation
        ↓
stock fulfilment and/or Work Order
```

Primary rule:

> **Sales records customer demand. The ERP decides whether the demand belongs in production planning.**

---

# 2. Final Review Result

The original plan had the correct UX direction but required the following repo-specific corrections before it could safely remain 10/10.

## 2.1 Corrected — DR quantity double-count risk

The earlier formula proposed:

```text
RemainingForNewDo(SO)
    ↓
convert to production UOM
    ↓
subtract ALL active DR allocations
```

This is unsafe in the current repo.

`SaSoLineReserve.SumDoQtyAsync(...)` already includes every non-deleted SO-linked DO in `NEW`, `POSTED`, or `CLOSED`, including DO lines that carry `DeliveryRequestSourceId`.

Therefore, once DR demand has already moved into a DR-linked DO:

- `RemainingForNewDo(...)` has already reduced SO availability for that DO;
- subtracting the full DR allocation again would count part of the same demand twice.

This revised plan introduces an explicit **outstanding DR reservation** calculation:

```text
Outstanding DR Qty per source
    = max(DR Allocated Production Qty - DR-linked DO Std Qty, 0)
```

Only that still-outstanding DR quantity is subtracted from the SO's current unassigned production capacity.

---

## 2.2 Corrected — Draft DR can become stale before Release

Current DR Release:

`SaDeliveryRequestService.ExecuteLifecycleAsync(... Release ...)`

locks the DR and sources but does not revalidate:

- current SO status;
- current revision;
- current item MfgType;
- ACTIVE Product Definition;
- current outstanding SO capacity;
- direct DO/direct invoice activity created after the draft;
- DefinitionCode still being ACTIVE.

The revised plan adds **release-time server revalidation** before a DRAFT becomes RELEASED.

---

## 2.3 Corrected — direct DO can consume demand already controlled by a released DR

`SaDoService.ValidateDoSoReserveAsync(...)` protects normal SO quantity capacity using `SaSoLineReserve`, but it currently does not subtract outstanding demand already controlled by a `RELEASED` / `IN_PRODUCTION` DR.

A direct DO therefore needs a server-side DR-capacity gate.

DR-linked DO lines must continue using their existing exact `DeliveryRequestSourceId` validation and must **not** be double-subtracted.

---

## 2.4 Confirmed — direct SO invoice already has a DR guard

`SaInvoiceService` already blocks direct SO invoicing when the exact SO line is controlled by an active DR in:

- `RELEASED`;
- `IN_PRODUCTION`.

Do not replace this logic.

This plan only aligns the SO billable picker so the UI does not offer a line that the invoice service will later reject.

---

## 2.5 Corrected — SO production-sensitive fields are not protected by active DR lineage

Current SO update protection is driven mainly by:

- delivered quantity;
- invoiced quantity;
- shipped quantity.

An SO line with an active DR source can still be changed in ways that invalidate DR lineage unless explicitly protected.

This plan adds server-side protection for:

- item;
- warehouse;
- required delivery date;
- frozen UOM conversion basis;
- line deletion;
- customer;
- project.

Quantity remains editable only within a safe floor.

---

## 2.6 Corrected — persisted SO line identity must not be renumbered

Current `SaSo.razor.cs` calls `Renumber()` after add/edit/delete.

For an existing Sales Order, `SaSoService.PrepareLinesAsync(...)` treats positive `Line` as the persisted detail key.

Exact DR lineage also uses:

```text
SONo + CustRel + SOLine
```

Therefore persisted line numbers must never be changed merely because a different UI row is removed.

This plan separates:

- persisted line identity;
- client/grid row identity;
- display sequence.

---

## 2.7 Corrected — legacy UOM conversion cannot blindly default to 1

New SO saves calculate:

```csharp
StdQty = OrderQty * StdPsize
```

However existing/migrated rows may have:

```text
StdPsize = 0
OrderQty > 0
StdQty > 0
```

The current DR test seed already contains such a shape.

Therefore the production conversion factor must resolve as:

```text
if StdPsize > tolerance:
    factor = StdPsize
else if OrderQty > tolerance AND StdQty > tolerance:
    factor = StdQty / OrderQty
else:
    conversion cannot be safely resolved
```

Never silently assume `1` when a persisted SO snapshot provides a different ratio.

---

## 2.8 Corrected — Product Definition selection must not pick an arbitrary first row

Current DR UI effectively selects the first ACTIVE definition even when multiple ACTIVE definitions exist and none is the default.

Approved behaviour:

- one ACTIVE definition → auto-select it;
- multiple ACTIVE with one default → auto-select the default;
- multiple ACTIVE with no default → leave blank and require the planner to choose;
- no ACTIVE definition → demand is not releasable.

---

# 3. Confirmed Current Repository Facts

## 3.1 Existing manufacturing classification

File:

`ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`

Existing:

```csharp
public string MfgType { get; set; } = "BUY";
```

Existing tokens are defined in:

`ErpWeb.Model/Entities/Planning/PrBomHdr.cs`

```text
BUY
MAKE
PHANTOM
```

No new `MfgType` database column is required.

---

## 3.2 Product Definition already owns MAKE promotion

File:

`ErpWeb.Core/Planning/PrProductDefService.cs`

Current behaviour promotes:

```text
BUY → MAKE
```

when the first Product Definition/BOM is saved.

Therefore:

```text
MAKE + no ACTIVE Product Definition
```

is a valid current repository state and must be treated as:

> **Manufacturing setup incomplete**

---

## 3.3 ACTIVE Product Definition identity

Files:

- `ErpWeb.Model/Entities/Planning/PrBomHdr.cs`
- `ErpWeb.Model/Configurations/Planning/PrBomHdrConfiguration.cs`

Selection identity is company-level:

```text
CompanyCode
ProdCode
DefinitionCode
```

with one ACTIVE revision per logical definition.

`BranchCode` / `LocationCode` are not Product Definition selection keys.

Use:

```csharp
db.PrBomHdrs
```

for Product Definition checks.

---

## 3.4 SO statuses

File:

`ErpWeb.Core/Sales/SaSoCalc.cs`

Current status constants:

```text
NEW
SHIPPED
CLOSED
SUPERSEDED
```

Direct DR demand is allowed only from the current revision with:

```text
NEW
SHIPPED
```

---

## 3.5 SO quantity snapshots

File:

`ErpWeb.Core/Sales/SaSoService.cs`

New SO lines freeze:

```csharp
StdPsize
StdQty
StdUom
SellingUom
```

Current save calculation:

```csharp
var stdPsize = item.StdPackSize is > 0m ? item.StdPackSize.Value : 1m;
var stdQty = SaSoQty.RoundQty(orderQty * stdPsize);
```

These frozen values must be respected after a line becomes DR-controlled.

---

## 3.6 Existing SO fulfilment authority

File:

`ErpWeb.Core/Sales/SaSoLineReserve.cs`

Existing helpers:

```csharp
SaSoLineReserve.SumBySoLinesAsync(...)
SaSoLineReserve.Evaluate(...)
SaSoLineReserve.RemainingForNewDo(...)
SaSoLineReserve.RemainingForNewSoInv(...)
```

Do not invent a second SO delivery/invoice reservation formula.

---

## 3.7 Existing exact DR → DO lineage

`SaDoDetail` already stores:

```csharp
DeliveryRequestSourceId
```

`SaDoService.PrepareLinesAsync(...)` already validates an exact DR source against:

- exact SO;
- exact revision;
- exact SO line;
- customer;
- product;
- production/standard UOM;
- warehouse;
- project;
- DR lifecycle;
- remaining source allocation.

Preserve this.

---

## 3.8 Existing DR fulfilment authority

File:

`ErpWeb.Core/Sales/SaDeliveryRequestFulfilmentService.cs`

The service already derives:

- `DeliveredQty`;
- `OpenDemandQty`;
- DR stock reservation;
- NEW shipment reservation;
- Work Order supply;
- `ProductionRequiredQty`;
- `ProductionUnplannedQty`.

This is the authority for deciding how much of a released DR actually needs production.

Do not make SO `MfgType=MAKE` mean “manufacture the whole SO”.

---

# 4. Approved Business Contracts

## BR-01 — Sales never selects MfgType

Do not put an editable:

```text
BUY / MAKE / PHANTOM
```

control in Sales Order.

---

## BR-02 — no Need DR checkbox

Do not add:

```text
Need DR
Production required
Create DR
```

as a normal SO-line decision.

---

## BR-03 — direct production eligibility

| MfgType | SO meaning | Direct DR candidate |
|---|---|---:|
| `BUY` | Stock / purchased | No |
| `MAKE` | Manufactured | Yes, if production setup is valid |
| `PHANTOM` | Internal / phantom | No |

---

## BR-04 — MAKE is not sufficient by itself

Direct DR candidate requires:

```text
current active Item Master
MfgType = MAKE
at least one ACTIVE PrBomHdr
current SO revision
SO status NEW or SHIPPED
positive remaining capacity
```

---

## BR-05 — Product Definition selection belongs to DR/Production planning

Sales does not select a Product Definition.

DR planner rules:

```text
1 ACTIVE
    → auto-select

multiple ACTIVE + one default
    → auto-select default

multiple ACTIVE + no default
    → planner must choose

0 ACTIVE
    → cannot release DR
```

---

## BR-06 — MAKE means eligible, not full production

Correct:

```text
SO customer demand
   ↓
DR controlled demand
   ↓
stock reservation / shipment availability
   ↓
remaining shortage
   ↓
Work Order
```

Do not create WO directly from SO quantity.

---

## BR-07 — required delivery date

For a MAKE SO line:

- date present → normal;
- date blank → non-blocking warning in SO.

SO save remains allowed.

Do not force ordinary Sales users into Production setup work.

---

## BR-08 — warehouse

Keep current behaviour:

```text
Item DefWarehouse
     ↓
default SO Warehouse
```

Sales may change it while the line is not DR-controlled.

Once an active DR source exists for that exact SO line, Warehouse is frozen.

---

## BR-09 — project

Keep SO header `ProjId`.

Once any active DR source exists on the SO revision, Project cannot be changed without first removing/cancelling the active DR control.

---

## BR-10 — customer

Once any active DR source exists on the SO revision, Customer cannot be changed.

This preserves `SaDeliveryRequestSource.CustomerCode` lineage.

---

## BR-11 — no automatic DR creation from SO save

Never implement:

```text
SO Save → create DR
```

DR must remain a consolidation/planning layer across SO demand.

---

# 5. Authoritative Capacity Model

This section is mandatory. The Code Agent must implement this math exactly in one shared server-side authority.

## 5.1 Exact SO-line key

All demand capacity is keyed by:

```text
CompanyCode
BranchCode
SONo
CustRel
SOLine
```

Case-insensitive comparison for document/item string codes must follow current repository conventions.

---

## 5.2 Frozen production conversion factor

Create a deterministic helper.

Concept:

```csharp
ResolveFrozenStdFactor(
    decimal stdPsize,
    decimal orderQty,
    decimal stdQty)
```

Rules:

```text
if StdPsize > tolerance
    factor = StdPsize

else if OrderQty > tolerance AND StdQty > tolerance
    factor = StdQty / OrderQty

else
    unresolved
```

Then:

```text
ProductionQty = RoundQty(SalesQty × factor)
SalesQty      = RoundQty(ProductionQty ÷ factor)
```

If the conversion is unresolved:

- do not assume 1;
- do not expose the line as safely DR-eligible;
- mutation/release must reject with a clear legacy conversion message.

---

## 5.3 Baseline remaining SO capacity

Use existing:

```csharp
var eval = SaSoLineReserve.Evaluate(...);
var remainingSalesQty = SaSoLineReserve.RemainingForNewDo(eval);
```

This value already accounts for the existing SO/DO/invoice reservation rules.

Convert it to production units using the frozen factor.

Call the result:

```text
OpenProductionDemandQty
```

---

## 5.4 DR-linked DO quantity

For each active `SaDeliveryRequestSource`, sum non-deleted linked DO detail `StdQty` where:

```text
SaDoDetail.DeliveryRequestSourceId = source.UID
```

and DO status is:

```text
NEW
POSTED
CLOSED
```

Call:

```text
LinkedDoProductionQty
```

Do not include deleted DOs.

---

## 5.5 Outstanding DR reservation

Per DR source:

```text
OutstandingDrProductionQty
    = max(
        AllocatedProductionQty - LinkedDoProductionQty,
        0)
```

Calculate this **per source first**.

Then aggregate by exact SO line.

Do not aggregate allocation and linked DO independently before clamping.

---

## 5.6 Available quantity for another DR

For DR candidate/create/update:

```text
AvailableForDr
    = max(
        OpenProductionDemandQty
        - OtherActiveDrOutstandingProductionQty,
        0)
```

Where:

- candidate list: `OtherActive...` means all active DR sources;
- create: all active DR sources;
- edit current draft: exclude the current DeliveryRequestId;
- release current draft: exclude the current DeliveryRequestId.

This avoids double-counting DR-linked DO quantity.

---

## 5.7 Full DR allocation remains the SO edit floor

For an existing SO line controlled by active DR source(s):

```text
MinimumProductionQty
    = SUM(active SaDeliveryRequestSource.AllocatedProductionQty)
```

SO quantity may be reduced only if the recomputed frozen `StdQty` remains at or above that full active DR allocation.

Do not use only outstanding DR reservation for the SO edit floor.

The DR allocation is an audit/control commitment even if some of it has already moved to DO.

---

# 6. New Shared Core Helpers

## 6.1 `ErpWeb.Core/Sales/SaProductionDemandRules.cs`

Create a small deterministic rules class.

Required responsibilities:

- production-demand state constants;
- SO status eligibility;
- MfgType eligibility;
- state mapping;
- frozen factor resolution;
- sales ↔ production conversion.

Recommended states:

```text
STOCK_OR_PURCHASED
AUTO_PRODUCTION
SETUP_REQUIRED
INTERNAL_PHANTOM
```

Use current repository constants:

```text
SaSoStatuses
PrMfgTypes
```

Do not put database access in this class.

---

## 6.2 `ErpWeb.Core/Sales/SaSoDeliveryRequestCapacity.cs`

Create one shared DB-backed capacity helper.

Purpose:

> Prevent `SaDeliveryRequestService`, `SaSoService`, and `SaDoService` from implementing different DR reservation math.

Recommended result keyed by exact SO revision/line:

```csharp
public sealed record SaSoDrCapacityFacts(
    decimal ActiveAllocatedProductionQty,
    decimal LinkedDoProductionQty,
    decimal OutstandingProductionQty);
```

Required batch loader inputs:

```text
AppDbContext
CompanyCode
BranchCode
SO numbers / exact keys
optional excludeDeliveryRequestId
reservation lifecycle mode
CancellationToken
```

Lifecycle modes:

### All active DR sources

Used by:

- DR candidate list;
- creating another DR;
- editing a DR while excluding current ID.

Includes active source rows regardless of DRAFT/RELEASED state because existing DR drafts already reserve against another DR draft.

### Committed DR only

Used by direct fulfilment protection.

Include source rows whose DR is:

```text
RELEASED
IN_PRODUCTION
```

Do not let a DRAFT DR permanently block normal direct shipment.

Release-time revalidation closes the race between DRAFT planning and direct fulfilment.

### Query requirements

- batch query;
- no N+1;
- company + branch scoped;
- exact source ID to DO linkage;
- `SaDo.DeletedAtUtc == null`;
- DO lifecycle filter `NEW/POSTED/CLOSED`;
- per-source clamp before SO-line aggregation.

---

# 7. Item Master Changes

## 7.1 `ErpWeb.Core/Inventory/IvMasterResults.cs`

Add current `MfgType` to:

```text
IvStockMasterEditVm
IvStockMasterListRow
```

Use `"BUY"` as DTO default if avoiding a Planning namespace dependency in the DTO file.

---

## 7.2 `ErpWeb.Core/Inventory/IvStockMasterService.cs`

Update:

```text
MapEditVm(...)
MapListRow(...)
```

to return existing persisted `IvStockMaster.MfgType`.

### Do not make it generally editable

Do not add `MfgType` to:

```text
ApplyEditableFields(...)
```

The general Item Master screen is not allowed to arbitrarily change MAKE/BUY/PHANTOM.

Product Definition remains the manufacturing setup authority.

---

## 7.3 `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`

Add read-only:

> **Supply method**

Display:

```text
BUY      → Purchased / Stock
MAKE     → Manufactured
PHANTOM  → Internal / Phantom
```

Hint:

> Supply method is controlled by Production Definition setup.

Use plain display markup or validation-disabled read-only controls.

---

## 7.4 `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

Update:

```text
CreateBlank()
Clone(...)
```

### Critical copy rule

Current page supports copying another Item Master.

When:

```text
CopyFrom = MAKE item
```

the copied screen must **not** carry MAKE into the new item display because Product Definition/BOM is not copied and Item Master save does not write MfgType.

After clone-for-copy explicitly set:

```text
Model.MfgType = BUY
```

before displaying/saving the new item.

---

## 7.5 Optional Item Master list column

Files:

```text
IvStockMasterList.razor
IvStockMasterList.razor.cs
```

Add compact read-only `Supply` column.

Do not add sorting unless repository sort mappings are updated consistently.

---

# 8. Shared Item Lookup Changes

## `ErpWeb.Core/Inventory/IvInventoryLookupService.cs`

Extend:

```csharp
IvStockMasterLookupRow
```

with:

```csharp
public string MfgType { get; init; } = "BUY";
```

Update:

```csharp
MapStock(...)
```

to map persisted `MfgType`.

Do not join Product Definition tables into the generic Inventory item lookup.

---

# 9. SO Production-Demand Read Model

## 9.1 `ErpWeb.Core/Sales/ISaSoService.cs`

Extend `SaSoLineDto` with derived/non-persisted fields:

```text
MfgType
ActiveProductionDefinitionCount
ProductionDemandState
```

Do **not** add these to `SaSoLineRequest`.

Do **not** persist them to `SaSoDetail`.

---

## 9.2 Add lightweight production hint DTO

Add:

```csharp
SaSoProductionDemandInfo
```

Recommended fields:

```text
ICode
MfgType
ActiveDefinitionCount
ProductionDemandState
```

Expose a service method conceptually:

```csharp
GetProductionDemandInfoAsync(string itemCode, CancellationToken ct)
```

Purpose:

- after Sales selects a MAKE item;
- show `Production` vs `Setup required` immediately;
- no extra user decision.

This call is only necessary for MAKE item selection.

Failure to load the hint must not corrupt the SO; show a non-blocking status message and rely on server/DR validation.

---

## 9.3 `ErpWeb.Core/Sales/SaSoService.cs`

Add a batch loader for saved SO documents:

```text
item MfgType
ACTIVE Product Definition count
derived ProductionDemandState
```

Requirements:

- one batch item query;
- one batch ACTIVE-definition query;
- no N+1;
- company scoped for Product Definition;
- current master state only.

Update both `GetAsync(...)` overloads so each `SaSoLineDto` receives production state.

Historical SO commercial snapshots must remain unchanged.

---

# 10. SO Line Identity Hardening

This is mandatory because DR traceability uses exact `SOLine`.

## 10.1 Current problem

`SaSo.razor.cs` currently runs:

```csharp
Renumber();
```

after line add/edit/delete.

`SaSoService.PrepareLinesAsync(...)` treats positive `Line` as persisted PK identity during edit.

Therefore display renumbering must not mutate persisted line identity.

---

## 10.2 `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`

Add a UI-only unique row key to `SaSoLineVm`, e.g.:

```csharp
public Guid ClientKey { get; set; } = Guid.NewGuid();
```

Rules:

- `Clone()` preserves `ClientKey`;
- `FromDto()` creates one;
- newly added rows create one;
- `Line` is no longer used as client-grid identity.

Remove persisted-line mutation from `Renumber()`.

For an existing SO:

- loaded lines retain their database `Line`;
- new lines retain `Line = 0`;
- server already treats `Line = 0` as a new line.

---

## 10.3 `ErpWeb.UI/Sales/Transactions/SaSo.razor`

Change grid key:

```text
KeyFieldName = ClientKey
```

Do not use `Line` as grid key because multiple unsaved edit-mode rows can legitimately have `Line=0`.

For the visual `#` column, display row sequence from the current list index.

Display sequence is cosmetic.

Persisted `Line` is lineage identity.

---

# 11. SO Active-DR Edit Protection

## 11.1 Server authority — `SaSoService.UpdateAsync(...)`

Before rewriting existing SO details, batch-load active `SaDeliveryRequestSource` rows for the current:

```text
Company
Branch
SONo
CustRel
```

Group by exact SO line.

If any active DR source exists on the SO revision:

### Header fields

Block change to:

```text
CustCode
ProjId
```

Reason:

- DR source snapshots CustomerCode;
- DR grouping/header uses ProjectCode.

### DR-controlled line

Block:

```text
delete line
change ICode
change Warehouse
change DeliveryDate
change persisted line identity
```

Preserve frozen production conversion snapshot:

```text
SellingUom
StdUom
StdPsize / derived frozen factor
```

Do not silently refresh these fields from today's Item Master when the line is DR-controlled.

### Quantity

Quantity increase:

```text
allowed
```

Quantity decrease:

```text
allowed only if:
new frozen StdQty
    >= total active DR AllocatedProductionQty

AND current existing delivered/invoiced/shipped floors remain satisfied
```

### Commercial fields

Fields not used by DR lineage may remain editable under existing SO rules, including normal pricing/remarks fields, unless another existing rule blocks them.

---

## 11.2 UI protection — `SaSo.razor.cs`

Use the already loaded:

```text
SaSoDocument.DeliveryRequests
```

to determine whether an exact persisted line has an active DR source.

For an active DR-controlled line:

- item picker disabled;
- warehouse disabled;
- delivery date disabled;
- delete disabled;
- quantity remains enabled;
- show:
  > Controlled by Delivery Request — item, warehouse and required date are locked.

If any active DR source exists on the SO:

- Customer control disabled;
- Project control disabled;
- show concise explanation.

Server rules remain authoritative; UI is convenience only.

---

# 12. SO Entry UX

## 12.1 `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`

Extend `SaSoLineVm` with:

```text
MfgType
ActiveProductionDefinitionCount
ProductionDemandState
ClientKey
```

Update:

```text
Clone()
FromDto()
OnPopupItemClearedAsync()
OnPopupItemSelectedAsync(...)
```

Do not include production-derived fields in `ToRequest()`.

---

## 12.2 Item selection

On item selection:

1. existing code continues to populate normal commercial fields;
2. copy `MfgType` from item lookup;
3. if MAKE, call the lightweight SO production-demand info service;
4. render production hint.

Sales does not select anything.

---

## 12.3 SO line popup

Show read-only compact status:

### BUY

> 📦 Stock / purchased item

### MAKE + ACTIVE definition

> 🏭 Manufactured item — eligible for production planning.

### MAKE + no ACTIVE definition

> ⚠ Manufacturing setup incomplete — no ACTIVE Product Definition.

### PHANTOM

> Internal / phantom item — not direct customer production demand.

---

## 12.4 Delivery date warning

When a MAKE line has no `DeliveryDate`:

> Required delivery date is recommended for manufactured items.

Warning only.

Do not block SO save solely for this.

---

## 12.5 SO line grid

Add compact `Supply` column:

```text
Stock
Production
Setup required
Internal
```

Keep the existing detailed Production demand trace section.

---

## 12.6 CSS

File:

`ErpWeb.UI/Sales/Transactions/SaSo.razor.css`

Add only isolated/local styles.

Follow existing Sales transaction UI.

---

# 13. DR Eligible Demand Hardening

## `SaDeliveryRequestService.ListEligibleSalesOrderDemandAsync(...)`

Push cheap eligibility predicates into the base query before `.Take(1000)` where possible.

Required base conditions:

```text
Company / Branch scope
header.IsCurrent
header.Status = NEW or SHIPPED
active Item Master exists
item.MfgType = MAKE
at least one ACTIVE PrBomHdr exists
detail.StdQty > 0
StdUom present
item code present
```

`PrBomHdr` eligibility is company-level.

Do not filter Product Definition by branch/location.

---

## 13.1 Load SO reservation facts

Batch call:

```csharp
SaSoLineReserve.SumBySoLinesAsync(...)
```

once for the candidate SO set.

---

## 13.2 Load DR capacity facts

Batch call the new:

```text
SaSoDeliveryRequestCapacity
```

using all active DR sources.

---

## 13.3 Per-line calculation

Resolve frozen factor.

Calculate:

```text
RemainingSalesQty
    = RemainingForNewDo(eval)

OpenProductionDemandQty
    = RemainingSalesQty × frozen factor

AvailableForDr
    = OpenProductionDemandQty
      - ActiveDrOutstandingProductionQty
```

Clamp to zero using repository quantity rounding/tolerance.

---

## 13.4 Preserve DTO semantics

Do **not** silently redefine the existing:

```text
ProductionDemandQty
```

to mean a dynamic remaining quantity.

Keep:

```text
ProductionDemandQty = persisted SaSoDetail.StdQty
```

Add explicit dynamic fields to `SaDeliveryRequestEligibleSource`:

```text
OpenProductionDemandQty
ActiveDrLinkedDoQty
ActiveDrOutstandingQty
AvailableForDr
```

Keep:

```text
ActiveDrAllocatedQty
```

as full active source allocation.

This preserves audit/read-model meaning.

---

# 14. DR Draft Create / Update Hardening

## `SaDeliveryRequestService.PrepareSourceRowsAsync(...)`

The mutation path is authoritative.

For every source revalidate under the existing transaction:

1. exact SO exists;
2. current revision;
3. SO status `NEW` or `SHIPPED`;
4. exact SO line exists;
5. Item Master exists and active;
6. current MfgType is `MAKE`;
7. at least one ACTIVE Product Definition exists;
8. frozen conversion resolves;
9. SO remaining capacity from `SaSoLineReserve`;
10. other active DR outstanding capacity;
11. selected allocation does not exceed `AvailableForDr`;
12. product/UOM/warehouse/project invariants remain.

### Current draft edit

When updating the current DRAFT DR:

```text
exclude current DeliveryRequestId
```

from the “other DR” capacity calculation.

Do not count the current draft against itself.

---

## 14.1 Product Definition validation during draft save

If `request.DefinitionCode` is supplied:

- it must match an ACTIVE definition for the selected product.

If omitted:

- one ACTIVE → server may resolve it;
- multiple + one default → server may resolve default;
- multiple + no default → allow DRAFT to remain unselected;
- zero ACTIVE → source is invalid.

Do not accept arbitrary/inactive DefinitionCode.

---

# 15. DR Release-Time Revalidation

## `SaDeliveryRequestService.ExecuteLifecycleAsync(... Release ...)`

Before changing:

```text
DRAFT → RELEASED
```

revalidate the full demand contract.

Required checks:

1. source list still exists and positive;
2. every source SO is current;
3. SO status still NEW/SHIPPED;
4. exact line still exists;
5. item still active;
6. MfgType still MAKE;
7. frozen conversion still valid;
8. current direct DO/direct invoice reservations are respected;
9. other active DR outstanding reservations are respected;
10. current source allocation still fits capacity;
11. DefinitionCode resolves to an ACTIVE Product Definition;
12. existing product/UOM/warehouse/project consistency still holds.

If stale:

> reject Release and keep the DR in DRAFT.

Do not silently shrink quantities on Release.

Planner must review/update the draft.

---

## 15.1 Release definition resolver

At Release:

```text
header.DefinitionCode supplied
    → must still be ACTIVE

header.DefinitionCode blank + one ACTIVE
    → resolve it

header.DefinitionCode blank + multiple ACTIVE + one default
    → resolve default

header.DefinitionCode blank + multiple ACTIVE + no default
    → reject: select Product Definition

zero ACTIVE
    → reject
```

Persist the resolved DefinitionCode before commit.

---

# 16. Release Concurrency / Lock Contract

This is critical.

Current DRAFT update already takes DR locks before `PrepareSourceRowsAsync(...)` reaches SO locks.

For Release:

1. keep current DR header/source locking;
2. collect exact source SO numbers;
3. lock current SO headers in deterministic `SaSoLockOrder.Comparer` order before final capacity validation;
4. then read reservation/capacity facts and validate;
5. transition to RELEASED only after validation succeeds.

This serializes Release against direct DO/direct invoice saves, which already lock SO headers.

Because new SO locking increases deadlock exposure, extend the Release lifecycle path with the same bounded SQL Server deadlock retry policy used by DR create/update:

```text
retry SQL error 1205
bounded attempts
return concurrency message after final retry
```

Do not remove existing row-version checks.

Do not loosen `UPDLOCK / HOLDLOCK` behaviour already used for DR source allocation protection.

---

# 17. DR Entry Product Definition UX

File:

`ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`

Fix `LoadDefinitionOptionsAsync(...)`.

Current code effectively chooses the first row even when selection is ambiguous.

Replace with:

```text
if exactly one option
    choose it

else if one IsDefault
    choose default

else
    DefinitionCode = null
```

The Product Definition ComboBox remains the planner's explicit choice.

If no ACTIVE definition:

- show clear warning;
- Release disabled or Release returns server validation;
- do not invent `STANDARD`.

Server validation remains authoritative.

---

# 18. Direct DO Protection

## 18.1 `SaDoService.ValidateDoSoReserveAsync(...)`

Keep existing `SaSoLineReserve` validation.

For **direct SO DO lines only**:

```text
DeliveryRequestSourceId == null
```

load committed DR capacity:

```text
RELEASED / IN_PRODUCTION only
```

Calculate:

```text
OpenProductionDemandQty
    = RemainingForNewDo(existing eval with this DO excluded)
      × frozen factor

DirectDoAvailableProductionQty
    = max(
        OpenProductionDemandQty
        - CommittedDrOutstandingProductionQty,
        0)
```

Compare this DO's prepared:

```text
StdQty
```

against the available production quantity.

Reject if exceeded.

### Do not apply this extra subtraction to DR-linked DO lines

A DR-linked DO already has exact source-specific validation:

```text
DeliveryRequestSourceId
source allocation remaining
```

Applying both would double-count.

---

## 18.2 `SaSoService.GetRemainingLinesAsync(...)`

This method feeds direct SO → DO selection.

Align UI with server behaviour.

For each SO line:

1. get existing `RemainingForNewDo`;
2. resolve frozen factor;
3. convert committed outstanding DR production qty back to sales qty;
4. reduce the displayed direct-DO available quantity.

Do not offer quantity that `SaDoService` will reject.

DRAFT DR does not reduce direct-DO picker availability.

If direct fulfilment consumes the demand, the DRAFT DR will be caught at Release revalidation.

---

# 19. Direct Invoice Picker Alignment

## `SaSoService.GetBillableLinesAsync(...)`

`SaInvoiceService` already blocks direct SO invoicing for exact SO lines controlled by:

```text
RELEASED
IN_PRODUCTION
```

Align the picker:

- if exact line has committed active DR source, do not return it for direct SO invoicing.

Do not alter the existing invoice server rule.

DRAFT DR remains non-committed and does not block direct invoice selection.

Release-time revalidation handles a stale draft.

---

# 20. DR Detail / Trace Semantics

## `SaDeliveryRequestService.BuildDetailAsync(...)`

Keep:

```text
ProductionDemandQty = frozen total SaSoDetail.StdQty
SourceQty = source SO order quantity snapshot
AllocatedProductionQty = DR source allocation
```

Do not rewrite these historic meanings.

Use the shared capacity helper for dynamic fields:

```text
OpenProductionDemandQty
ActiveAllocatedProductionQty
ActiveDrLinkedDoQty
ActiveDrOutstandingQty
AvailableForDr
```

Do not calculate dynamic availability as:

```text
StdQty - full active DR allocation
```

once linked DO activity exists.

---

# 21. SO Production Trace

File:

`SaSoService.LoadDeliveryRequestTraceAsync(...)`

Preserve existing exact:

```text
SONo / CustRel / SOLine → DR
```

trace in this plan.

Do not reinterpret the existing `ProductionDemandQty` as a dynamic outstanding quantity.

Any future redesign of per-source Work Order attribution is outside this plan.

---

# 22. No Database Migration Required

This approved design adds no persistent schema field.

Do not add:

```text
SaSoDetail.MfgType
SaSoDetail.NeedDR
SaSoDetail.ProductionPlanningMode
```

Do not add another MfgType column.

Existing persistent authority remains:

```text
IvStockMaster.MfgType
PrBomHdr
SaSo / SaSoDetail
SaDeliveryRequestSource
SaDoDetail.DeliveryRequestSourceId
```

All new production state/capacity fields are read-model/DTO/helper values.

---

# 23. DevExpress Safety Contract

Follow current project binding rules.

Manual callback binding requires expression metadata.

Example:

```razor
<DxTextBox Text="@Model.Value"
           TextChanged="@OnValueChanged"
           TextExpression="@(() => Model.Value)" />
```

```razor
<DxSpinEdit Value="@Model.Qty"
            ValueChanged="@OnQtyChanged"
            ValueExpression="@(() => Model.Qty)" />
```

Or use standard `@bind-*`.

For display-only production status:

- prefer plain markup;
- or set `ValidationEnabled="false"` on read-only DevExpress editors.

Do not introduce missing `TextExpression` / `ValueExpression` runtime errors.

---

# 24. Tests — Mandatory

## 24.1 Existing DR test seed must be upgraded

File:

`ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`

Current seed creates SO demand but does not seed:

- `IvStockMaster` MAKE item;
- ACTIVE `PrBomHdr`.

After this implementation, seed both.

Preserve at least one legacy conversion case where:

```text
StdPsize = 0
OrderQty > 0
StdQty > 0
```

to prove the fallback uses:

```text
StdQty / OrderQty
```

and not `1`.

---

## 24.2 Production-demand rules tests

Add tests for:

1. BUY → STOCK_OR_PURCHASED.
2. MAKE + active definition → AUTO_PRODUCTION.
3. MAKE + no active definition → SETUP_REQUIRED.
4. PHANTOM → INTERNAL_PHANTOM.
5. eligible status NEW.
6. eligible status SHIPPED.
7. CLOSED rejected.
8. SUPERSEDED rejected.
9. factor uses positive StdPsize.
10. legacy factor derives `StdQty / OrderQty`.
11. unresolved factor is rejected.
12. production ↔ sales conversion rounding is stable.

---

## 24.3 Item Master tests

Files:

```text
IvStockMasterServiceTests.cs
IvStockMasterLargeLookupTests.cs
```

Cases:

1. new item defaults BUY;
2. Get/Edit returns MfgType;
3. list returns MfgType;
4. generic Item Master save cannot tamper MAKE back to BUY;
5. item lookup returns MfgType;
6. exact resolve returns MfgType;
7. copied MAKE item starts new item as BUY;
8. existing concurrency tests pass.

---

## 24.4 SO tests

File:

`ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs`

Cases:

1. saved BUY state mapped correctly.
2. MAKE + ACTIVE state mapped correctly.
3. MAKE + no ACTIVE definition reports setup required.
4. PHANTOM reports internal.
5. production state is not persisted to `SaSoDetail`.
6. blank DeliveryDate on MAKE does not alone block SO save.
7. active DR blocks customer change.
8. active DR blocks project change.
9. active DR line cannot be deleted.
10. active DR line item cannot change.
11. active DR line warehouse cannot change.
12. active DR line delivery date cannot change.
13. quantity may increase.
14. quantity may decrease only when frozen StdQty stays >= full active DR allocation.
15. DR-controlled line preserves frozen SellingUom/StdUom/conversion factor.
16. inactive/cancelled DR source does not block.
17. removing an unrelated UI row does not change persisted Line identity.
18. adding multiple new lines to an existing SO keeps existing positive line IDs and sends new rows as Line=0.
19. direct DO remaining-line picker subtracts committed outstanding DR only.
20. direct invoice billable picker hides committed DR-controlled lines.
21. draft DR does not block direct picker.
22. existing pricing/totals/revision/concurrency tests remain green.

---

## 24.5 DR eligibility tests

File:

`ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`

Cases:

1. NEW + MAKE + ACTIVE definition → eligible.
2. SHIPPED + remaining capacity → eligible.
3. BUY excluded.
4. PHANTOM excluded.
5. inactive item excluded.
6. MAKE without ACTIVE definition excluded.
7. CLOSED excluded.
8. SUPERSEDED/historical excluded.
9. fully consumed SO excluded.
10. unresolved legacy conversion excluded/rejected clearly.

---

## 24.6 DR double-count tests

Mandatory:

### A — no DR-linked DO

```text
SO open production = 100
DR1 allocated = 60
linked DO = 0
```

Expected:

```text
DR1 outstanding = 60
available for another DR = 40
```

### B — DR-linked NEW DO

```text
SO = 100
DR1 allocated = 60
DR-linked DO StdQty = 20
```

`SaSoLineReserve` already sees the DO.

Expected:

```text
SO baseline open after DO = 80
DR1 outstanding = 40
available for another DR = 40
```

It must **not** become 20.

### C — posted DR-linked DO

Same expectation after DO post/delivery:

```text
available for another DR = 40
```

### D — direct DO plus DR

```text
SO = 100
direct DO = 10
DR allocation = 60
DR-linked DO = 0
```

Expected:

```text
available for another DR = 30
```

### E — source-level clamp

If legacy inconsistency has:

```text
linked DO > source allocation
```

outstanding source reservation clamps to zero and must never become negative.

---

## 24.7 DR draft mutation tests

1. over-allocation rejected.
2. current draft edit excludes itself.
3. another draft DR still reserves demand.
4. same SO source cannot be duplicated in one DR.
5. warehouse mismatch rejected.
6. project mismatch rejected.
7. product/UOM mismatch rejected.
8. supplied DefinitionCode must be ACTIVE.
9. multiple active/no default may save draft with no DefinitionCode.
10. current revision identity remains exact.

---

## 24.8 DR Release tests

1. valid draft releases.
2. SO becomes CLOSED after draft → Release rejected.
3. SO revised after draft → Release rejected.
4. item becomes non-MAKE → rejected.
5. item inactive → rejected.
6. Product Definition becomes inactive/superseded with no valid replacement → rejected.
7. supplied DefinitionCode no longer ACTIVE → rejected.
8. blank DefinitionCode + one ACTIVE → resolved.
9. blank + default ACTIVE among multiple → default resolved.
10. blank + multiple ACTIVE no default → rejected.
11. direct DO consumes demand after draft → stale Release rejected if allocation no longer fits.
12. direct invoice consumes capacity after draft → stale Release rejected if allocation no longer fits.
13. another DR allocation consumes capacity → stale Release rejected.
14. failed Release leaves status DRAFT.
15. deadlock retry returns deterministic concurrency error after final attempt.

---

## 24.9 DO tests

File:

`ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`

1. direct DO cannot consume production capacity reserved by RELEASED DR.
2. direct DO cannot consume production capacity reserved by IN_PRODUCTION DR.
3. DRAFT DR does not block direct DO.
4. DR-linked DO continues to use exact source allocation.
5. DR-linked DO is not double-subtracted by direct-DO DR gate.
6. linked DO quantity reduces outstanding DR reservation.
7. deleted DO does not consume outstanding DR source capacity.
8. existing SO reserve tests remain green.

---

## 24.10 Invoice tests

File:

`ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs`

Keep existing server behaviour.

Add/retain:

1. direct SO invoice blocked by RELEASED DR.
2. direct SO invoice blocked by IN_PRODUCTION DR.
3. DRAFT DR does not block.
4. DO-linked invoice path remains unchanged.

---

# 25. Regression Suite

At minimum run:

```text
ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterLargeLookupTests.cs
ErpWeb.Tests/Planning/Master/PrProductDefServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs
```

Then run the complete solution tests.

No implementation is accepted if it breaks:

- SO pricing;
- SO tax/totals;
- SO revision rules;
- SO line reservation;
- DO posting;
- invoice allocation;
- exact SO/DR/DO lineage;
- DR stock reservation;
- WO allocation;
- production posting;
- inventory costing;
- production costing;
- rollback controls.

---

# 26. Implementation Sequence

## Phase 1 — pure rules and capacity authority

1. create `SaProductionDemandRules.cs`;
2. create `SaSoDeliveryRequestCapacity.cs`;
3. unit-test frozen conversion and outstanding DR math.

Do not touch UI before this authority is stable.

---

## Phase 2 — master/read-model plumbing

1. Item Master DTO MfgType;
2. Item Master service mapping;
3. Inventory lookup MfgType;
4. Item Master read-only Supply UI;
5. copy-from MAKE → new BUY correction.

Run Inventory + Product Definition tests.

---

## Phase 3 — SO line identity

1. add `ClientKey`;
2. grid key moves from persisted `Line` to `ClientKey`;
3. stop renumbering persisted line IDs;
4. display sequence becomes UI-only;
5. new edit rows stay `Line=0`.

Run SO add/edit/delete tests before production-demand changes.

---

## Phase 4 — SO production state UX

1. `SaSoLineDto` production state;
2. batch loader;
3. lightweight MAKE selection hint;
4. `SaSoLineVm` state;
5. badges/warnings;
6. no production selector.

Run SO service/UI verification.

---

## Phase 5 — SO active-DR edit integrity

1. server active-source lookup;
2. customer/project lock;
3. line item/warehouse/date/delete locks;
4. frozen UOM conversion preservation;
5. quantity floor;
6. UI mirrors server rules.

Run SO/DR lineage regression.

---

## Phase 6 — DR candidate + draft mutation

1. status/MfgType/item/active-definition gates;
2. batch `SaSoLineReserve`;
3. batch shared DR capacity;
4. correct `AvailableForDr`;
5. draft definition validation;
6. BuildDetail dynamic capacity fields.

Run DR tests.

---

## Phase 7 — DR Release integrity

1. release-time source revalidation;
2. lock SO headers deterministically;
3. capacity revalidation;
4. deterministic definition resolution;
5. bounded deadlock retry.

Run concurrency/stale-draft tests.

---

## Phase 8 — direct fulfilment alignment

1. direct DO committed-DR capacity gate;
2. `GetRemainingLinesAsync` picker alignment;
3. `GetBillableLinesAsync` invoice picker alignment;
4. preserve DR-linked DO and invoice existing paths.

Run DO + Invoice tests.

---

## Phase 9 — full regression

Exercise manually:

```text
SO
→ DR Draft
→ DR Release
→ stock reservation
→ WO when shortage exists
→ DR-linked DO
→ invoice
```

Also test:

```text
SO
→ DR Draft
→ direct DO before Release
→ DR Release revalidation
```

and:

```text
SO
→ Released DR
→ attempt direct DO
→ blocked/netted correctly
```

---

# 27. Manual Acceptance Scenarios

## Scenario A — BUY item

Expected:

```text
SO shows Stock
SO saves normally
not offered for DR
```

---

## Scenario B — MAKE + ACTIVE definition

Expected:

```text
SO shows Production
Sales makes no production decision
eligible for DR
```

---

## Scenario C — MAKE + no ACTIVE definition

Expected:

```text
SO allowed
shows Setup required warning
not releasable into DR production flow
```

---

## Scenario D — multiple Product Definitions

### one default

Expected:

```text
DR auto-selects default
```

### no default

Expected:

```text
DR planner must select
no arbitrary first choice
```

---

## Scenario E — partial direct fulfilment before DR

```text
SO 100
direct DO 20
```

Expected DR candidate:

```text
80 production-basis demand before other DR reservations
```

using frozen conversion.

---

## Scenario F — DR-linked DO no double count

```text
SO 100
DR1 = 60
DR-linked DO = 20
```

Expected another DR availability:

```text
40
```

not 20.

---

## Scenario G — stale draft

```text
DR Draft allocates 80
direct fulfilment later consumes 40
```

If only 60 now fits:

```text
Release rejected
DR remains Draft
planner reviews quantity
```

---

## Scenario H — SO edit after active DR

Expected:

```text
item locked
warehouse locked
delivery date locked
customer/project locked
line cannot delete
quantity may increase
quantity decrease cannot fall below active DR allocation
```

---

## Scenario I — SO UI deletes an unrelated row

Expected:

```text
DR-linked persisted SOLine remains unchanged
exact lineage remains valid
```

---

# 28. Acceptance Checklist

Implementation is accepted only when all are true:

- [ ] Sales never chooses BUY/MAKE/PHANTOM.
- [ ] Sales has no Need DR checkbox.
- [ ] Item Master exposes Supply Method read-only.
- [ ] Copying a MAKE item does not misleadingly copy MAKE into a new item.
- [ ] Generic Item Master save cannot arbitrarily change MfgType.
- [ ] SO lookup carries MfgType.
- [ ] SO immediately explains Stock / Production / Setup required / Internal.
- [ ] MAKE without ACTIVE Product Definition is visible but does not block SO save.
- [ ] persisted SO Line identity is never UI-renumbered.
- [ ] active DR prevents destructive SO master/line changes.
- [ ] DR-controlled SO quantity cannot fall below full active DR allocation.
- [ ] DR-controlled UOM conversion basis remains frozen.
- [ ] DR candidate accepts only current NEW/SHIPPED active MAKE demand with ACTIVE Product Definition.
- [ ] legacy conversion never blindly falls back to factor 1.
- [ ] `ProductionDemandQty` retains frozen-total semantics.
- [ ] dynamic open/available quantities have explicit fields.
- [ ] DR outstanding reservation subtracts DR-linked DO quantity per source.
- [ ] linked DO is never double-counted.
- [ ] draft edit excludes its own DR from “other reservation” calculation.
- [ ] Release revalidates stale SO/master/capacity state.
- [ ] Release resolves only deterministic ACTIVE Product Definition.
- [ ] multiple active/no-default definitions require planner selection.
- [ ] direct DO cannot steal committed DR demand.
- [ ] DR-linked DO is not double-blocked.
- [ ] direct SO invoice existing DR guard remains.
- [ ] direct DO/invoice pickers match server rules.
- [ ] DRAFT DR does not unnecessarily block normal direct fulfilment.
- [ ] stale DRAFT DR is caught at Release.
- [ ] no automatic DR is created from SO save.
- [ ] no automatic WO is created from SO save.
- [ ] SO → DR → WO → DO exact lineage remains.
- [ ] posting/costing logic is untouched.
- [ ] mandatory regression tests pass.

---

# 29. Code Agent Guardrails

The Code Agent must:

1. re-open each named file before modifying it;
2. implement server authority before UI;
3. preserve `.razor` + `.razor.cs` code-behind pattern;
4. preserve isolated CSS;
5. preserve tenant/company/branch filters;
6. preserve current row-version checks;
7. preserve current SQL locking patterns;
8. add bounded deadlock handling where Release now acquires SO locks;
9. use `SaSoLineReserve` for SO reservation facts;
10. use the new shared DR-capacity helper everywhere DR outstanding quantity is needed;
11. never subtract full DR allocation after `RemainingForNewDo` without accounting for DR-linked DO;
12. never compute `StdQty - DeliveredQty`;
13. never assume legacy `StdPsize=0` means factor 1 when `StdQty/OrderQty` is available;
14. preserve exact `SONo + CustRel + SOLine` identity;
15. never use display row number as persisted SO line identity;
16. never trust UI eligibility at mutation/release;
17. never silently choose an arbitrary Product Definition;
18. not add persistent SO planning flags;
19. not alter costing/posting/rollback logic;
20. stop and report a repo conflict rather than inventing a new business rule.

---

# 30. Non-Goals

Do not expand this implementation into:

- DR mass-grouping UI/date buckets;
- cross-customer grouping policy;
- customer-specific manufacturing engine;
- fresh-batch override;
- MRP;
- procurement planning;
- PHANTOM authoring redesign;
- production costing changes;
- inventory costing changes;
- e-Invoice changes;
- Work Order scheduling redesign.

Those are separate plans.

---

# 31. Approval

## APPROVED FOR IMPLEMENTATION — 10/10

Approved against:

```text
mokth/net10projectTemplate
branch: productionv2
HEAD: 1b806666b9518bff6760239cf7b31b9d10530c81
```

This revised plan is approved because it now covers the full control path rather than only the SO screen:

```text
master setup
    ↓
SO entry UX
    ↓
stable SO line identity
    ↓
SO edit integrity
    ↓
correct DR candidate capacity
    ↓
DR draft mutation validation
    ↓
DR release-time revalidation
    ↓
direct DO/invoice conflict prevention
    ↓
existing DR fulfilment / WO authority
```

Final invariant:

> **An SO line can be easy for Sales to enter without making Sales responsible for production decisions, while the server still guarantees that the same customer demand cannot be planned, delivered, or reserved twice through competing paths.**
