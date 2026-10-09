# Sales Order Production-Demand Facilitation
## APPROVED FOR IMPLEMENTATION — `productionv2`

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified baseline:** `1b806666b9518bff6760239cf7b31b9d10530c81`  
**Baseline commit:** `check in dr again`  
**Review status:** **APPROVED FOR IMPLEMENTATION**  
**Implementation confidence:** **10/10 for the verified baseline**

> Approval is tied to the verified baseline above. If any file named in this plan changes before implementation, re-diff those files first and preserve the business contracts below.

---

# 1. Objective

Make Sales Order entry production-aware **without requiring ordinary Sales users to understand or choose BUY / MAKE / PHANTOM, Delivery Request, MRP, BOM, or Work Order concepts**.

Normal Sales Order entry must remain:

> Customer → Item → Quantity → Warehouse → Required Delivery Date → Save

The system must determine production eligibility from controlled master/planning data.

Required behaviour:

1. `IvStockMaster.MfgType` remains the master/system-owned supply method.
2. Sales Order does **not** expose an editable `MfgType` field.
3. Sales Order does **not** add a `Need DR` checkbox.
4. `MAKE` means **eligible for production planning**, not “produce the full SO quantity”.
5. `BUY` is not direct production demand.
6. `PHANTOM` is not direct customer production demand.
7. A `MAKE` item is only DR-eligible when an **ACTIVE Product Definition** exists.
8. Sales users get a simple read-only supply/planning indicator and useful warnings.
9. DR candidate quantity must be based on **current outstanding SO demand**, not the original full `StdQty`.
10. Existing SO → DR → WO traceability must remain intact.

---

# 2. Confirmed Repository Baseline

## 2.1 Existing manufacturing master field

File:

`ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`

Confirmed field:

```csharp
public string MfgType { get; set; } = "BUY";
```

Confirmed meaning in current source:

- `BUY`
- `MAKE`
- `PHANTOM`

Configuration already exists:

`ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs`

```csharp
builder.Property(e => e.MfgType)
    .HasMaxLength(10)
    .HasDefaultValue("BUY")
    .ValueGeneratedNever();
```

**No new database column is required for `MfgType`.**

---

## 2.2 Current Item Master does not expose `MfgType`

Confirmed affected files:

- `ErpWeb.Core/Inventory/IvMasterResults.cs`
- `ErpWeb.Core/Inventory/IvStockMasterService.cs`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

`IvStockMasterEditVm` currently does not carry `MfgType`.

`IvStockMasterService.MapEditVm`, `MapListRow`, and the Item Master UI currently do not expose the field.

This makes an important manufacturing master value invisible to ordinary ERP users.

---

## 2.3 Current shared item lookup does not return `MfgType`

File:

`ErpWeb.Core/Inventory/IvInventoryLookupService.cs`

Confirmed DTO:

`IvStockMasterLookupRow`

It currently returns item/UOM/warehouse/stock/pricing fields but not `MfgType`.

Confirmed mapper:

`MapStock(...)`

also omits `MfgType`.

Sales Order uses `IvStockMasterPicker` and receives `IvStockMasterLookupRow`, so the SO page currently cannot display the item's supply method immediately after selection.

---

## 2.4 Product Definition already controls manufacturing behaviour

File:

`ErpWeb.Core/Planning/PrProductDefService.cs`

Confirmed current behaviour:

- Product Definition reads `IvStockMaster.MfgType`.
- A first Product Definition save currently promotes `BUY → MAKE`.
- BOM validation requires `MAKE` or `PHANTOM`.
- Active Product Definitions are represented by `PrBomHdr.Status == PrBomStatuses.Active`.

**This plan preserves that existing Product Definition ownership.**

Do **not** add an unrestricted BUY / MAKE / PHANTOM selector to Sales Order.

Do **not** add an unrestricted editable supply-method combo to Item Master as part of this plan.

---

## 2.5 Current Sales Order entry is already simple

Files:

- `ErpWeb.UI/Sales/Transactions/SaSo.razor`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`
- `ErpWeb.Core/Sales/ISaSoService.cs`
- `ErpWeb.Core/Sales/SaSoService.cs`

Current line popup already captures:

- Item
- Description
- Warehouse
- Customer item
- Order quantity
- Delivery date
- ETD / ETA
- pricing/tax/discount
- Remarks

Current item selection method:

`OnPopupItemSelectedAsync(IvStockMasterLookupRow item)`

already auto-populates:

- item code
- description
- standard UOM
- selling UOM
- pack size
- stock control
- classification
- item default warehouse

This is the correct place to add a **read-only supply hint**, not another required decision.

---

## 2.6 SO already has production traceability

`ISaSoService.cs` already exposes `SaSoDeliveryRequestTrace`.

`SaSo.razor` already displays a **Production demand trace** section with:

- Delivery Request
- exact revision / line
- product
- production demand
- DR allocation
- WO allocation
- produced quantity
- status

Preserve this.

The new SO supply indicator is a summary/UX aid only; it must not replace exact traceability.

---

## 2.7 Current DR eligibility is too broad

File:

`ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

Method:

`ListEligibleSalesOrderDemandAsync(...)`

Current query requires:

- current SO revision;
- positive `StdQty`;
- `StdUom`;
- item code;

but currently does **not** enforce:

- SO lifecycle status `NEW` / `SHIPPED`;
- item `MfgType == MAKE`;
- ACTIVE Product Definition;
- true remaining SO quantity.

Current calculation:

```csharp
AvailableForDr = Unplanned(x.ProductionDemandQty, allocated)
```

where `ProductionDemandQty` is currently `detail.StdQty`.

This can expose the original full SO production quantity even when part of the SO has already been fulfilled/reserved.

---

## 2.8 Current source-save validation repeats the same full-quantity assumption

File:

`ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

Method:

`PrepareSourceRowsAsync(...)`

Current code uses:

```csharp
var available = Unplanned(detail.StdQty, existingAllocated);
```

This must be hardened together with the eligible-source query.

The picker and final server-side mutation must use the same authority.

---

## 2.9 SO quantity/UOM conversion is known and must be respected

File:

`ErpWeb.Core/Sales/SaSoService.cs`

Current SO save logic freezes:

```csharp
var stdPsize = item.StdPackSize is > 0m ? item.StdPackSize.Value : 1m;
var stdQty = SaSoQty.RoundQty(orderQty * stdPsize);
```

Therefore:

- `OrderQty`, `DeliveredQty`, DO reservations, invoice reservations are sales/order-unit quantities.
- `StdQty` is production/standard-unit quantity.

**Never subtract `DeliveredQty` directly from `StdQty`.**

For remaining production demand:

1. derive authoritative remaining SO quantity in order/sales units;
2. convert it using the frozen SO line `StdPsize`;
3. then subtract existing active DR production allocations.

---

# 3. Approved Business Rules

## BR-01 — Sales users never choose `MfgType`

No BUY / MAKE / PHANTOM input in Sales Order.

`MfgType` is controlled by master/planning setup.

---

## BR-02 — No `Need DR` checkbox

Do not add:

```text
Need DR = Yes / No
```

to SO header or line.

Sales users enter customer demand, not downstream document mechanics.

---

## BR-03 — Normal supply interpretation

| MfgType | Sales interpretation | Direct DR candidate |
|---|---|---:|
| `BUY` | Stock / purchased | No |
| `MAKE` | Manufactured | Yes, subject to ACTIVE definition and outstanding demand |
| `PHANTOM` | Internal / phantom | No |

---

## BR-04 — ACTIVE Product Definition required

`MAKE` alone is not enough.

A direct SO production candidate requires at least one:

```text
PrBomHdr
CompanyCode = current company
ProdCode = SO item
Status = ACTIVE
```

Rules:

- 0 ACTIVE definitions → not DR-selectable; SO shows **Manufacturing setup incomplete**.
- 1 ACTIVE definition → DR candidate.
- multiple ACTIVE definitions → DR candidate; existing DR/WO definition-selection rules remain responsible for choosing the definition.

Do not silently choose a definition in Sales Order.

---

## BR-05 — SO lifecycle eligibility

Direct production demand may come only from the current SO revision with:

- `NEW`
- `SHIPPED`

Reject/exclude:

- `CLOSED`
- `SUPERSEDED`
- non-current revisions

`SHIPPED` must remain eligible when a line still has remaining unfulfilled quantity.

---

## BR-06 — Authoritative remaining customer demand

Use existing:

`SaSoLineReserve.SumBySoLinesAsync(...)`

and:

`SaSoLineReserve.Evaluate(...)`

with zero “this document” quantities.

Use:

```csharp
SaSoLineReserve.RemainingForNewDo(eval)
```

as the authoritative remaining SO quantity available for further fulfilment/planning.

Then convert:

```text
RemainingProductionQty =
    RoundQty(RemainingSalesQty * FrozenStdPsize)
```

where `FrozenStdPsize` is the persisted `SaSoDetail.StdPsize`.

Then:

```text
AvailableForDr =
    max(RemainingProductionQty - ActiveDrAllocatedProductionQty, 0)
```

Do not derive production demand by:

```text
StdQty - DeliveredQty
```

because they may be different UOM bases.

---

## BR-07 — `MAKE` does not mean “produce everything”

Example:

```text
SO demand             100 PCS
Existing FG / DR stock reservation covers part of the demand
```

SO remains a production-planning candidate.

The existing DR fulfilment/reservation layer determines stock-covered quantity versus actual production-required quantity.

Do not auto-create WO quantity from the SO line quantity.

---

## BR-08 — Delivery date is important but must not punish Sales entry

For a `MAKE` item:

- if `DeliveryDate` exists: display normally;
- if blank: show a non-blocking warning:
  **“Required delivery date is recommended for manufactured items.”**

Do not block SO save solely because the date is blank.

The DR/planner must be able to see that the demand has no required date and review it.

---

## BR-09 — Warehouse behaviour remains unchanged

Keep the existing SO behaviour:

```text
Item DefWarehouse
    ↓
SO line Warehouse default
```

Sales may change the warehouse using the existing control.

Warehouse remains part of DR compatibility/grouping.

---

## BR-10 — Project behaviour remains unchanged

Keep the existing SO header `ProjId`.

DR continues deriving/checking project from the SO source.

Do not add a duplicate production project field.

---

## BR-11 — No automatic DR creation on SO save

Never implement:

```text
Save SO → automatically create one DR
```

This would prevent consolidation across multiple SOs.

Correct flow:

```text
SO demand
  ↓
eligible production demand pool
  ↓
DR consolidation/planning
  ↓
Work Order
```

---

# 4. Scope

## Included

1. Surface existing `MfgType` safely as read-only supply information.
2. Carry `MfgType` through shared item lookup.
3. Show simple supply/planning status in Sales Order.
4. Show non-blocking manufacturing warnings.
5. Derive current SO production state for loaded SO lines.
6. Harden DR eligible-source filtering.
7. Harden final DR source validation.
8. Correct DR candidate quantity to use outstanding SO demand in the correct UOM.
9. Add regression tests.

---

# 5. Non-Goals

Do **not** implement in this plan:

- DR mass-grouping UI / date-bucket UI;
- cross-customer grouping rules;
- `AllowCrossCustomerDrGrouping`;
- automatic DR creation;
- automatic Work Order creation;
- MRP;
- procurement planning;
- changes to production costing;
- changes to inventory costing;
- changes to posting / rollback logic;
- fresh-batch / force-production override;
- customer-specific manufacturing specification engine;
- unrestricted manual `MfgType` editing;
- PHANTOM authoring redesign;
- Sales pricing changes;
- e-Invoice changes.

These can be implemented separately after this foundation is stable.

---

# 6. Shared Production-Demand Rule

Create:

`ErpWeb.Core/Sales/SaProductionDemandRules.cs`

Purpose: one small deterministic rules class so SO UI/service and DR service do not invent different rules.

Recommended contents:

```csharp
public static class SaProductionDemandStates
{
    public const string StockOrPurchased = "STOCK_OR_PURCHASED";
    public const string AutoProduction = "AUTO_PRODUCTION";
    public const string SetupRequired = "SETUP_REQUIRED";
    public const string InternalPhantom = "INTERNAL_PHANTOM";
}

public static class SaProductionDemandRules
{
    public static bool IsEligibleSoStatus(string? status) =>
        status is SaSoStatuses.New or SaSoStatuses.Shipped;

    public static bool IsDirectProductionItem(string? mfgType) =>
        PrMfgTypes.Normalize(mfgType) == PrMfgTypes.Make;

    public static string ResolveState(string? mfgType, int activeDefinitionCount)
    {
        var normalized = PrMfgTypes.Normalize(mfgType);

        if (normalized == PrMfgTypes.Phantom)
            return SaProductionDemandStates.InternalPhantom;

        if (normalized != PrMfgTypes.Make)
            return SaProductionDemandStates.StockOrPurchased;

        return activeDefinitionCount > 0
            ? SaProductionDemandStates.AutoProduction
            : SaProductionDemandStates.SetupRequired;
    }

    public static decimal ToProductionQty(decimal salesQty, decimal stdPsize) =>
        SaSoQty.RoundQty(Math.Max(salesQty, 0m) * (stdPsize > 0m ? stdPsize : 1m));
}
```

Agent may adjust namespace/usings to match the repo, but **must preserve the rules**.

Do not add database access to this static class.

---

# 7. File-by-File Implementation

## 7.1 `ErpWeb.Core/Inventory/IvMasterResults.cs`

### `IvStockMasterEditVm`

Add:

```csharp
public string MfgType { get; set; } = PrMfgTypes.Buy;
```

If adding a Planning dependency here is undesirable, use `"BUY"` as the DTO default but normalize in the service.

### `IvStockMasterListRow`

Add:

```csharp
public string MfgType { get; init; } = "BUY";
```

This is presentation data only.

---

## 7.2 `ErpWeb.Core/Inventory/IvStockMasterService.cs`

Update:

- `MapListRow(...)`
- `MapEditVm(...)`

to return the persisted `IvStockMaster.MfgType`.

For a new item, preserve default:

```text
BUY
```

### Critical protection

Do **not** add `MfgType` to `ApplyEditableFields(...)`.

A posted/tampered Item Master edit request must not arbitrarily change manufacturing supply type through the general Item Master screen.

Existing Product Definition behaviour remains the manufacturing authority.

---

## 7.3 `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

Update:

- `CreateBlank()`
- `Clone(...)`

to carry `MfgType`.

New item display defaults to `BUY`.

Add helper:

```text
BUY      → Purchased / Stock
MAKE     → Manufactured
PHANTOM  → Internal / Phantom
```

Unknown/legacy values must display safely as `Unknown` and must not throw.

---

## 7.4 `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`

Add read-only field:

**Supply method**

Display:

- `Purchased / Stock`
- `Manufactured`
- `Internal / Phantom`

Add hint:

> Supply method is controlled by Production Definition setup.

Do not render an editable ComboBox.

In New mode show:

> Purchased / Stock

without asking the user to choose.

---

## 7.5 Optional but approved: Item Master list

Files:

- `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor.cs`

Add compact read-only **Supply** column.

Do not add complicated filtering in this change.

If sorting is added, add `MfgType` to `IvStockMasterSortFields.Allowed` and repository sort handling consistently. Otherwise keep the column non-sortable.

---

## 7.6 `ErpWeb.Core/Inventory/IvInventoryLookupService.cs`

### `IvStockMasterLookupRow`

Add:

```csharp
public string MfgType { get; init; } = "BUY";
```

### `MapStock(...)`

Add:

```csharp
MfgType = x.MfgType
```

This is additive and must not change search/paging behaviour.

Do not add Product Definition joins to the shared Inventory lookup.

---

## 7.7 `ErpWeb.Core/Sales/ISaSoService.cs`

Extend `SaSoLineDto` with **derived, non-persisted** planning metadata:

```csharp
public string MfgType { get; init; } = "BUY";
public int ActiveProductionDefinitionCount { get; init; }
public string ProductionDemandState { get; init; } =
    SaProductionDemandStates.StockOrPurchased;
```

These fields describe current planning/master state.

They are **not snapshots** and must not be written to `SaSoDetail`.

Do not add them to `SaSoLineRequest`.

---

## 7.8 `ErpWeb.Core/Sales/SaSoService.cs`

Add a private batch loader, conceptually:

```csharp
LoadProductionDemandInfoAsync(
    AppDbContext db,
    string companyCode,
    IEnumerable<string> itemCodes,
    CancellationToken ct)
```

It must:

1. normalize/distinct item codes;
2. query `IvStockMaster` once for their current `MfgType`;
3. query ACTIVE `PrBomHdr` records once;
4. count ACTIVE definitions per product;
5. resolve `ProductionDemandState` using `SaProductionDemandRules`;
6. return a dictionary keyed case-insensitively by item code.

**No N+1 queries.**

Update both `GetAsync(...)` overloads to load this dictionary before mapping the SO document.

Update `MapDocument(...)` / `MapLine(...)` signatures as needed so each `SaSoLineDto` receives:

- current `MfgType`;
- active definition count;
- production-demand state.

### Historical SO rule

Historical/superseded SO revisions may still display the **current** item supply classification because this information is explicitly planning/master state, not a frozen commercial snapshot.

Do not alter historical SO quantity/price/customer snapshots.

---

## 7.9 `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`

Extend `SaSoLineVm` with UI-only fields:

```csharp
public string MfgType { get; set; } = "BUY";
public int ActiveProductionDefinitionCount { get; set; }
public string ProductionDemandState { get; set; } =
    SaProductionDemandStates.StockOrPurchased;
```

Update:

- `Clone()`
- `FromDto(...)`

to copy them.

### Do not send them back in `ToRequest()`

They are not editable SO data.

### New item selection

In:

`OnPopupItemSelectedAsync(IvStockMasterLookupRow item)`

set:

```csharp
Popup.MfgType = PrMfgTypes.Normalize(item.MfgType);
```

For a newly selected unsaved item the shared lookup does not know ACTIVE definition count.

Therefore the immediate hint may say:

- BUY → `Stock / purchased item`
- MAKE → `Manufactured item — production planning applies`
- PHANTOM → `Internal / phantom item`

After reload, server-derived state can refine MAKE to `Setup required` when no ACTIVE definition exists.

Do not introduce an extra mandatory server call merely to render the badge.

### Clear handling

`OnPopupItemClearedAsync()` must reset supply/planning UI state to default.

---

## 7.10 `ErpWeb.UI/Sales/Transactions/SaSo.razor`

### Line popup

Immediately below Item / Description, add a compact read-only hint.

Examples:

**BUY**

> 📦 Stock / purchased item

**MAKE + normal/new selection**

> 🏭 Manufactured item — production will be planned automatically when required.

**MAKE + saved line + no ACTIVE definition**

> ⚠ Manufacturing setup incomplete — no active Product Definition.

**PHANTOM**

> Internal / phantom item — not direct production demand.

There must be **no checkbox, ComboBox or technical decision** here.

### Delivery-date warning

If line is MAKE and `DeliveryDate` is null:

> Required delivery date is recommended for manufactured items.

This is warning-only.

### SO line grid

Add one compact **Supply** column near Item / Delivery.

Recommended text:

- `Stock`
- `Production`
- `Setup required`
- `Internal`

Use short badge/chip styling.

Keep the existing detailed `Production demand trace` section unchanged.

---

## 7.11 `ErpWeb.UI/Sales/Transactions/SaSo.razor.css`

Add only local styles for:

- supply badge;
- production hint;
- warning state.

Do not introduce global CSS for this feature.

Follow existing Sales transaction visual language.

---

# 8. Harden DR Eligible Demand

## 8.1 `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`
### `ListEligibleSalesOrderDemandAsync(...)`

Change the candidate query/data loading so final results require:

```text
header.IsCurrent
AND header.Status IN (NEW, SHIPPED)
AND item.MfgType == MAKE
AND at least one ACTIVE Product Definition
AND positive remaining fulfilment quantity
AND positive remaining DR production quantity
```

Do not include:

- BUY;
- PHANTOM;
- CLOSED;
- SUPERSEDED;
- historical revision;
- MAKE with no ACTIVE definition.

### Batch reservation/fulfilment calculation

For the selected SO set call existing:

```csharp
SaSoLineReserve.SumBySoLinesAsync(...)
```

once, not once per row.

For each SO line:

```csharp
var eval = SaSoLineReserve.Evaluate(
    soNo,
    soLine,
    detail.OrderQty,
    detail.DeliveredQty,
    lineSums,
    thisDoQty: 0m,
    thisSoInvQty: 0m);

var remainingSalesQty = SaSoLineReserve.RemainingForNewDo(eval);

var remainingProductionQty =
    SaProductionDemandRules.ToProductionQty(
        remainingSalesQty,
        detail.StdPsize);

var availableForDr =
    Math.Max(
        remainingProductionQty - activeDrAllocatedQty,
        0m);
```

Populate:

```text
ProductionDemandQty = remainingProductionQty
AvailableForDr      = availableForDr
```

`ProductionDemandQty` must now mean **current outstanding production-basis demand**, not original full SO `StdQty`.

Keep original SO source/order quantity traceability unchanged.

---

# 9. Harden Final DR Mutation

## 9.1 `SaDeliveryRequestService.PrepareSourceRowsAsync(...)`

The mutation path is the final authority.

Do not trust the source-picker result.

Under the existing transaction/locking flow revalidate:

1. exact SO exists;
2. exact current revision;
3. SO status is `NEW` or `SHIPPED`;
4. exact SO line exists;
5. item still exists/active as required by current rules;
6. current item `MfgType == MAKE`;
7. at least one ACTIVE Product Definition exists;
8. outstanding sales quantity is still positive;
9. convert remaining sales qty through persisted `StdPsize`;
10. subtract active DR allocations;
11. requested DR allocation must not exceed the resulting quantity;
12. same product/UOM/warehouse/project invariants still hold.

Replace:

```csharp
Unplanned(detail.StdQty, existingAllocated)
```

with the new authoritative remaining-production calculation.

### Concurrency contract

Preserve current:

- SO lock;
- exact revision/line identity;
- DR allocation locking;
- row-version behaviour;
- deadlock retry;
- transaction boundaries.

Do not weaken the current over-allocation protection.

---

# 10. Source Trace Quantity Semantics

When a DR source is created:

- `SourceQty` continues to represent source SO order quantity as currently designed.
- `AllocatedProductionQty` is the DR quantity in production UOM.
- `ProductionDemandQty` in inquiry/eligible projections means current outstanding production-basis demand.
- Do not rewrite historical source quantities when later shipments occur.

This avoids destroying audit history.

---

# 11. No SQL Migration Required

This plan intentionally adds **no new database column**.

Existing:

`IvStockMaster.MfgType`

is already configured and persisted.

Do not add `MfgType` to `SaSoDetail`.

Do not add `NeedDR` to `SaSoDetail`.

Do not add `ProductionPlanningMode` in this implementation.

This keeps SO commercial snapshots clean and avoids a user-maintained planning flag.

---

# 12. DevExpress UI Safety Rules

The implementation must follow the repo's DevExpress binding rules.

For manual callback binding use expression metadata.

Example:

```razor
<DxTextBox Text="@Model.Value"
           TextChanged="@OnValueChanged"
           TextExpression="@(() => Model.Value)" />
```

and for value editors:

```razor
<DxSpinEdit Value="@Model.Qty"
            ValueChanged="@OnQtyChanged"
            ValueExpression="@(() => Model.Qty)" />
```

Alternatively use normal `@bind-*`.

For display-only supply information:

- prefer plain markup/badge where possible;
- if a DevExpress editor is used read-only, set `ValidationEnabled="false"`.

Do not introduce the recurring missing `TextExpression` / `ValueExpression` runtime error.

---

# 13. Tests

## 13.1 Inventory lookup tests

File:

`ErpWeb.Tests/Inventory/Master/IvStockMasterLargeLookupTests.cs`

Add coverage:

1. `MfgType=BUY` returned by item lookup.
2. `MfgType=MAKE` returned by item lookup.
3. paged lookup still bounded.
4. exact item resolve returns same `MfgType`.

---

## 13.2 Item Master service tests

File:

`ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs`

Add coverage:

1. new item defaults to BUY;
2. get/edit VM returns current MfgType;
3. list row returns current MfgType;
4. general Item Master save cannot tamper `MfgType` from MAKE back to BUY;
5. existing concurrency behaviour remains unchanged.

---

## 13.3 SO service tests

File:

`ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs`

Add coverage:

1. BUY line maps to `STOCK_OR_PURCHASED`.
2. MAKE + ACTIVE definition maps to `AUTO_PRODUCTION`.
3. MAKE + no ACTIVE definition maps to `SETUP_REQUIRED`.
4. PHANTOM maps to `INTERNAL_PHANTOM`.
5. mapping is batched/no behavioural change to SO save.
6. no derived production fields are persisted to `SaSoDetail`.
7. blank DeliveryDate does not make server save fail solely because item is MAKE.
8. existing pricing, totals, revision and row-version tests still pass.

---

## 13.4 Delivery Request service tests

File:

`ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`

Required cases:

### Eligibility

1. current NEW + MAKE + ACTIVE definition + outstanding qty → eligible.
2. current SHIPPED + MAKE + ACTIVE definition + remaining qty → eligible.
3. BUY → excluded.
4. PHANTOM → excluded.
5. MAKE without ACTIVE definition → excluded.
6. CLOSED SO → excluded.
7. SUPERSEDED/historical revision → excluded.
8. fully fulfilled line → excluded.
9. full active DR allocation → excluded.

### Correct quantity

10. Order 100, `StdPsize=1`, no fulfilment → production demand 100.
11. Order 100, 40 no longer available for new DO → production demand 60.
12. Order 10 BOX, `StdPsize=12 PCS`, remaining 4 BOX → production demand 48 PCS.
13. Existing active DR allocation 20 PCS against remaining production demand 48 → `AvailableForDr=28`.
14. Do not subtract `DeliveredQty` directly from `StdQty`.

### Mutation revalidation

15. line shown as eligible, then SO becomes CLOSED before DR save → save rejected.
16. item changes from MAKE to non-direct-production state before DR save → save rejected.
17. ACTIVE Product Definition disappears/becomes non-active before DR save → save rejected.
18. another DR allocates quantity first → second save cannot over-allocate.
19. warehouse mismatch still rejected.
20. project mismatch still rejected.
21. product/UOM mismatch still rejected.
22. exact SO revision/line trace remains unchanged.

---

# 14. Regression Tests That Must Remain Green

At minimum run:

```text
ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterLargeLookupTests.cs
ErpWeb.Tests/Planning/Master/PrProductDefServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs
```

Also run the full solution test suite before merge.

No change is approved if it breaks:

- SO pricing;
- SO quantity reservations;
- DO source allocation;
- invoice allocation;
- SO revision locking;
- DR exact-source traceability;
- DR fulfilment reservation;
- WO allocation;
- inventory posting;
- costing.

---

# 15. Implementation Sequence

Implement in this order.

### Step 1 — Supply metadata plumbing

- add MfgType to Item Master DTOs;
- map it in Item Master service;
- add it to `IvStockMasterLookupRow`;
- map it in `IvInventoryLookupService`.

Compile + inventory tests.

### Step 2 — Item Master read-only UX

- show Supply Method;
- optionally show Supply column on list;
- keep it non-editable.

Compile + Item Master tests.

### Step 3 — Shared production-demand rules

Create `SaProductionDemandRules.cs`.

Unit-test the deterministic rules.

### Step 4 — SO production state

- extend `SaSoLineDto`;
- batch-load item MfgType + ACTIVE definition count;
- map production state;
- extend `SaSoLineVm`.

Compile + SO service tests.

### Step 5 — SO UX

- supply badge;
- read-only manufacturing hint;
- non-blocking missing-delivery-date warning;
- no production selector.

Manual UI verification.

### Step 6 — DR eligibility hardening

- NEW/SHIPPED status gate;
- MAKE gate;
- ACTIVE definition gate;
- `SaSoLineReserve` outstanding quantity;
- standard-UOM conversion;
- active DR allocation deduction.

Run DR tests.

### Step 7 — DR mutation hardening

Revalidate every eligibility rule inside `PrepareSourceRowsAsync(...)`.

Run concurrency/over-allocation tests.

### Step 8 — Full regression

Run full solution tests and manually exercise SO → DR → WO trace.

---

# 16. Manual Verification Script

## Scenario A — Purchased item

1. Open Item Master for BUY item.
2. Confirm Supply Method = `Purchased / Stock`.
3. Create SO line.
4. Confirm SO shows `Stock`.
5. Save.
6. Open DR eligible-source picker.
7. Confirm line is absent.

Expected: **PASS**

---

## Scenario B — Manufactured item with ACTIVE definition

1. Use MAKE item with ACTIVE Product Definition.
2. Create SO qty 100.
3. Warehouse defaults automatically.
4. Enter delivery date.
5. Confirm SO hint says production is planned automatically.
6. Save.
7. Open DR eligible demand.

Expected:
- line is eligible;
- user never selected BUY/MAKE;
- user never selected Need DR.

---

## Scenario C — MAKE but setup incomplete

1. Use MAKE item with no ACTIVE definition.
2. Enter SO normally.
3. Confirm warning `Manufacturing setup incomplete`.
4. SO save remains allowed.
5. DR picker does not allow the line.

Expected: **PASS**

Sales is not blocked by a Production setup problem, but Production cannot accidentally plan an invalid item.

---

## Scenario D — Partial fulfilment

1. SO qty = 100.
2. Fulfil/reserve enough so only 40 remains available for new DO.
3. Open DR eligible demand.

Expected production demand = 40 × frozen `StdPsize`, not original 100 × pack size.

---

## Scenario E — UOM conversion

1. Sales UOM BOX.
2. `StdPsize=12`.
3. 4 BOX remain.

Expected DR production demand = 48 standard units.

---

## Scenario F — Concurrent allocation

1. Planner A loads eligible qty 100.
2. Planner B creates DR allocation 80.
3. Planner A tries to allocate 100 using stale screen.

Expected:
- server revalidation rejects over-allocation;
- no duplicate production demand is created.

---

# 17. Acceptance Criteria

Implementation is accepted only when all are true:

- [ ] Sales Order has no BUY/MAKE/PHANTOM selector.
- [ ] Sales Order has no Need DR checkbox.
- [ ] Normal SO item entry requires no additional production decision.
- [ ] Item Master visibly shows the existing supply method.
- [ ] General Item Master edit cannot arbitrarily overwrite `MfgType`.
- [ ] SO item lookup carries `MfgType`.
- [ ] SO shows simple user-friendly supply state.
- [ ] MAKE without ACTIVE Product Definition is visibly flagged.
- [ ] Missing delivery date for MAKE is warning-only.
- [ ] DR eligible query accepts only current NEW/SHIPPED direct MAKE demand with ACTIVE definition.
- [ ] BUY is not direct DR demand.
- [ ] PHANTOM is not direct DR demand.
- [ ] CLOSED/SUPERSEDED demand is excluded.
- [ ] DR quantity uses current remaining SO demand.
- [ ] Remaining sales qty is converted with persisted `StdPsize`.
- [ ] `DeliveredQty` is never directly subtracted from `StdQty`.
- [ ] Active DR allocations are deducted.
- [ ] Final DR save revalidates all rules.
- [ ] Existing product/UOM/warehouse/project grouping rules remain.
- [ ] Existing SO → DR → WO exact traceability remains.
- [ ] No automatic DR is created by SO save.
- [ ] No costing/posting behaviour is changed.
- [ ] Full regression suite passes.

---

# 18. Code-Agent Guardrails

The coding agent must:

1. inspect the verified files before editing;
2. preserve code-behind `.razor.cs` architecture;
3. use isolated/local CSS for SO UI changes;
4. follow existing DevExpress binding-expression requirements;
5. avoid N+1 queries;
6. keep tenant/company/branch scoping;
7. preserve row-version/concurrency checks;
8. preserve transaction/lock ordering;
9. reuse `SaSoLineReserve` instead of inventing another SO remaining-qty formula;
10. reuse `PrMfgTypes` and `PrBomStatuses`;
11. not create a parallel `MfgType` field;
12. not persist derived SO production state;
13. not silently expand scope into DR grouping UI or costing.

If implementation requires violating any rule above, stop that change and report the exact repo conflict instead of improvising.

---

# 19. Approval

## APPROVED FOR IMPLEMENTATION

This plan is approved against:

`mokth/net10projectTemplate` → `productionv2` → `1b806666b9518bff6760239cf7b31b9d10530c81`

The design intentionally keeps ordinary Sales Order entry simple while establishing a reliable server-side bridge:

```text
Item / Product Definition
        ↓
system-owned supply method
        ↓
Sales Order customer demand
        ↓
automatic production eligibility
        ↓
Delivery Request planning / consolidation
        ↓
Work Order
```

The key rule is:

> **Sales enters customer demand. The ERP decides whether that demand belongs in production planning.**
