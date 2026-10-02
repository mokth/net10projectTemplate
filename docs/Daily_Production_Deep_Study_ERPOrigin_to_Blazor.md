# Deep Reverse-Engineering Study — Daily Production
## ERPOrigin → net10projectTemplate (`production`)

**Legacy source:** `mokth/ERPOrigin`, branch `master`, commit `1282097754401af2b36ba7a0ab46cc83574e6253`  
**Target source:** `mokth/net10projectTemplate`, branch `production`, inspected current branch commit `8b573bac5a48b2180a5797fc01bd29857db9dc5c`  
**Study objective:** Extract the proven manufacturing domain logic from the legacy Daily Production implementation, identify its weaknesses, and define a production-safe architecture and implementation plan for the Blazor Server ERP.  
**Coding status:** **No implementation performed.**

---

# Evidence Labels

This report uses the following labels consistently:

- **CONFIRMED FROM CODE** — directly supported by inspected source code.
- **CONFIRMED FROM DATABASE LOGIC** — directly supported by SQL, EF mappings, transaction/update code, or table relationships in the repositories.
- **INFERRED — NEEDS VERIFICATION** — strongly suggested by code but not proven enough to present as a fact.
- **LEGACY BEHAVIOR — SHOULD NOT PORT** — observed legacy behavior that should not be copied as the new design.
- **RECOMMENDED NEW DESIGN** — proposed design for `net10projectTemplate`.

---

# 1. Executive Summary

## 1.1 What Daily Production actually is in ERPOrigin

**CONFIRMED FROM CODE**

Legacy Daily Production is not merely a shop-floor quantity-entry screen. It is a production execution transaction that connects:

```text
Work Order snapshot
    ↓
Work Centre
    ↓
Process
    ↓
Machine / operator / shift
    ↓
Actual output quantity
    ↓
Required process BOM / previous-process WIP
    ↓
Consume production-side material / WIP lot
    ↓
Create current-process output lot
    ↓
Update production history and cumulative progress
```

The central legacy execution documents are staged through:

```text
IvTrxBatch
IvTrxBatchDetail
```

with production transaction type:

```text
WI
```

The posted production facts then affect:

```text
PrSchDailyProd       — detailed Daily Production history
PrSchDailyProcess    — cumulative process production
WIPItemBalLoc        — production/WIP lot balance
PrSchMas             — Work Order status/progress
```

The important point is that the old ERP separates:

```text
Production definition
    ↓ copied to
Work Order snapshot
    ↓ executed by
Issue to Production / Daily Production
    ↓ accumulated in
WIP / Production Balance
    ↓ eventually received by
Finished Goods posting
```

Daily Production is therefore the **execution bridge between the frozen Work Order routing/BOM and production-side WIP movement**.

---

## 1.2 The user's conceptual model is substantially correct

**CONFIRMED FROM CODE**

The supplied conceptual flow is essentially the model implemented by ERPOrigin:

```text
normal inventory
      │
      │ Issue to Production (IP)
      ▼
production-side material / WIP balance
      │
      │ Daily Production (WI)
      ▼
process / work-centre output WIP lot
      │
      │ next process / work centre
      ▼
next WIP lot
      │
      ▼
final production output
      │
      │ separate FG receipt/posting
      ▼
normal finished-goods inventory
```

There is also a second legacy mode:

```text
normal inventory
      │
      │ Daily Production "No Issue" mode
      │ direct stock consumption/backflush
      ▼
production output / WIP
```

So ERPOrigin supports both:

1. **pre-issue then consume production WIP**, and
2. **direct warehouse backflush during production output**.

This distinction is represented in the new ERP much more cleanly by the existing material execution concepts:

```text
IssueMethod = MANUAL
IssueMethod = BACKFLUSH
```

The new system should use those explicit policies rather than reproducing separate Web Forms helper families.

---

## 1.3 Work Order snapshot is the production authority

**CONFIRMED FROM CODE**

Daily Production does **not** normally return to the current Product Definition to decide what material is required.

The legacy production calculation uses:

```text
PrSchBOMBL.GetBOM(
    workOrderNo,
    RelNo,
    WCCode,
    ProcessCode)
```

which reads the **Work Order BOM (`PrSchBOM`)**.

This is the correct domain rule:

> Once a Work Order is created/released, production execution must follow the Work Order snapshot, not a later-edited Product Definition.

The new ERP already implements this principle more strongly through:

```text
ProductionWorkOrder
ProductionWorkOrderRouteStep
ProductionWorkOrderOperation
ProductionWorkOrderMaterial
ProductionWorkOrderMachine
ProductionWorkOrderLabour
```

with snapshot revision/hash protection.

---

## 1.4 Exact legacy BOM requirement formula

**CONFIRMED FROM CODE**

In the main helper family, the effective process material requirement is:

```text
Total production basis
    = Good Output + Scrap Output + Reject Output

Material Required
    = BOM StdQty × Total production basis
      ÷ Work Centre scheduled quantity
```

Code equivalent:

```text
actQty =
    bomQty
    × (outputQty + scrapQty + rejectQty)
    ÷ cenPackSize
```

where current code obtains:

```text
cenPackSize = PrSchWCenter.ScheQty
```

Good/scrap/reject portions are also calculated separately:

```text
Good material qty
    = BOM StdQty × Good Output ÷ ScheQty

Scrap material qty
    = BOM StdQty × Scrap Output ÷ ScheQty

Reject material qty
    = BOM StdQty × Reject Output ÷ ScheQty
```

Quantities are normally rounded to **4 decimal places**.

If catch-weight is enabled:

```text
Required Weight = Required Qty × BOM DefCatchWt
```

also rounded to 4 decimals.

### Example

If the frozen Work Order process is:

```text
WO planned / WC ScheQty = 100 FG
RAW001 BOM Qty           = 200 PCS
RAW002 BOM Qty           = 50 KG
```

and Daily Production reports:

```text
Good   = 20
Scrap  = 2
Reject = 1
Total production basis = 23
```

legacy requirement is approximately:

```text
RAW001 total = 200 × 23 / 100 = 46 PCS
RAW002 total =  50 × 23 / 100 = 11.5 KG
```

while the good-only share is:

```text
RAW001 good = 200 × 20 / 100 = 40 PCS
RAW002 good =  50 × 20 / 100 = 10 KG
```

This proves the old implementation treats scrap/reject output as production effort that also consumes material.

---

## 1.5 New ERP already has a better material requirement formula

**CONFIRMED FROM CODE**

The current Blazor branch uses:

```text
RequiredQty
  = OperationMaterialBasisQty
    × ComponentQtyPerParent
    ÷ BomOutputQty
    × (1 + ScrapPercent / 100)
```

in:

```text
ErpWeb.Core/Production/WorkOrderQuantityCalculator.cs
CalculateMaterialAsync(...)
```

It then converts:

```text
RequiredQty / RequiredUom
        ↓
RequiredBaseQty / BaseUom
```

through the UOM conversion service.

Critically:

```text
RequiredQty does NOT include tolerance.
```

Tolerance is correctly treated as an **execution variance / issue-control limit**, not as part of the engineering requirement.

Therefore the new Daily Production implementation should **not recreate the legacy `PrSchBOM` arithmetic**. It should consume against the already-frozen and already-calculated:

```text
ProductionWorkOrderMaterial.RequiredQty
ProductionWorkOrderMaterial.RequiredBaseQty
ProductionWorkOrderMaterial.ConversionFactorToBase
```

and scale production consumption using the exact operation/output basis in the Work Order snapshot.

---

## 1.6 Production Balance is `WIPItemBalLoc`

**CONFIRMED FROM DATABASE LOGIC**

The primary legacy production balance-by-lot table is:

```text
dbo.WIPItemBalLoc
```

Its effective identity contains:

```text
ICode
WCCode
ProcessCode
LotNo
RevNo
ScheCode
RelNo
StdUom
```

and it stores important production lineage including:

```text
ICode
WCCode
ProcessCode
LotNo / RevNo
ProcessSeq
StdQty / StdUom
WtQty / WtUom
ScheCode / RelNo
WCICode
ProdCode
TransactionDate
TrxType
UnitPrice
```

The current new ERP still maps this table as:

```text
ErpWeb.Model/Entities/Production/WipItemBalLoc.cs
```

with an EF row-version concurrency column.

The legacy table is therefore the direct ancestor of the user's “Production Balance Lot” concept.

---

## 1.7 A later process really consumes the earlier output

**CONFIRMED FROM CODE**

`ProdPlanHelper.GetAllItemSql(...)` has explicit branches for:

1. issued stock material stored in WIP with `ProcessCode` containing `STOCK`;
2. output from the previous process in the same Work Centre;
3. output of a prior Work Centre's **FinalProcess**, when that output item is a BOM input of the current process.

The normal helper is restricted by:

```text
ScheCode = current Work Order
```

so the standard model is Work-Order-specific WIP lineage.

This implements the production chain:

```text
WC01 Process Final
   produces WIP001 Lot A
        ↓
WIPItemBalLoc
        ↓
WC02 BOM requires WIP001
        ↓
WC02 Daily Production consumes Lot A
        ↓
WC02 creates WIP002 Lot B
```

---

## 1.8 FinalProcess does not automatically mean normal inventory FG receipt

**CONFIRMED FROM CODE**

Daily Production posting (`WI`) creates/increments `WIPItemBalLoc`.

Finished-goods receipt is handled by a **separate legacy subsystem**, for example:

```text
ProductionPlan/HelperEx/DailyFGPostHelper.cs
```

which creates:

```text
TrxType = FGS
```

and calls:

```text
CPosting.PostInventoryTransaction("FGS", ...)
```

to update normal inventory and inventory history.

Therefore the old system's lifecycle is:

```text
Daily Production final process
        ↓
production-side WIP/output balance
        ↓
separate Finished Goods posting
        ↓
normal inventory
```

This separation should be preserved conceptually in the new ERP, even if the new UI later allows a combined “report final output + receive FG” command that executes atomically.

---

## 1.9 Current Blazor branch is already structurally prepared

**CONFIRMED FROM CODE**

The current `production` branch already contains:

```text
ProductionWorkOrder
ProductionWorkOrderRouteStep
ProductionWorkOrderOperation
ProductionWorkOrderMaterial

ProductionMaterialIssueLine
ProductionMaterialMovement
ProductionPostingLink

WipItemBalLoc
PrSchDailyProd

ProductionMaterialIssueService.*
ProductionMaterialAllocationService
ProductionMaterialExecutionCalc

InventoryAsOfStockService
IvInventoryPostingService
IvInventoryPostingService.Chronology
IvStockDateRules
```

Material Issue already supports:

- exact Work Order operation ownership;
- Work Order snapshot revision/hash checks;
- required / issued / returned / consumed quantity projections;
- issue tolerance;
- warehouse/location validation;
- multi-lot allocation;
- FEFO/FIFO proposal ordering;
- as-of-date stock;
- future-stock prevention;
- row locks (`UPDLOCK`, `HOLDLOCK`);
- atomic inventory posting;
- immutable production movement facts;
- posting idempotency links;
- rollback dependency protection;
- audit.

The correct Daily Production implementation is therefore **an extension of this architecture**, not a port of `DailyPrdBatchAll.aspx.cs`.

---

# 2. Legacy Daily Production Architecture

## 2.1 Main execution layers

```text
Web Forms UI
│
├─ DailyPrdBatchAll.aspx
├─ DailyPrdBatchAllEx.aspx
├─ DailyPrdBatchAny.aspx
├─ DailyProdAllView.aspx
└─ DailyInputView.aspx
        │
        ▼
Entry / selection helpers
│
├─ DailyProdHelper
├─ DailyPodOutPutHelper
├─ DailyPodOutPutNoIssueHelper
├─ DailyPodOutPutHelperAny
└─ DailyPodOutPutHelperAnyEx
        │
        ▼
Business / query helpers
│
├─ ProdPlanHelper
├─ ProdPlanHelperEx
├─ ProductionBL
├─ PrSchBOMBL
└─ WIPItemBalLocBL
        │
        ▼
Batch staging
│
├─ IvTrxBatch
└─ IvTrxBatchDetail
        │
        ▼
Posting
│
├─ DailyProdPostHelper
├─ DailyProdNoIssuePostHelper
└─ DailyProdJONGPostHelper
        │
        ├─ PrSchDailyProd
        ├─ PrSchDailyProcess
        ├─ WIPItemBalLoc
        ├─ PrSchMas
        └─ normal inventory in No-Issue mode
```

---

# 3. Complete Source File Dependency Map

## 3.1 Primary files

| Area | File | Role |
|---|---|---|
| Main UI | `ProductionPlan/ProdPlan/DailyPrdBatchAll.aspx` | Main Daily Production entry |
| Main code-behind | `ProductionPlan/ProdPlan/DailyPrdBatchAll.aspx.cs` | Search, selection, batch creation, save/post orchestration |
| Output view | `ProductionPlan/ProdPlan/DailyProdAllView.aspx.cs` | Daily Production view/search |
| Input view | `ProductionPlan/ProdPlan/DailyInputView.aspx.cs` | Production input/WIP view |
| Main row builder | `ProductionPlan/Helper/DailyProdHelper.cs` | Converts selected production/WIP rows to `IvTrxBatchDetail` |
| Main auto-output helper | `ProductionPlan/HelperEx/DailyPodOutPutHelper.cs` | Standard issue/WIP mode; calculates input material |
| No-Issue helper | `ProductionPlan/HelperEx/DailyPodOutPutNoIssueHelper.cs` | Direct warehouse material mode |
| Any helper | `ProductionPlan/HelperEx/DailyPodOutPutHelperAny.cs` | More permissive WIP sourcing |
| AnyEx helper | `ProductionPlan/HelperEx/DailyPodOutPutHelperAnyEx.cs` | Extra permissive/special exception path |
| Main poster | `ProductionPlan/Helper/DailyProdPostHelper.cs` | Posts WI to production history + WIP |
| No-Issue poster | `ProductionPlan/Helper/DailyProdNoIssuePostHelper.cs` | Posts raw inventory IP + WI production in one legacy path |
| Query helper | `ProductionPlan/Helper/ProdPlanHelper.cs` | Resolves issued stock / previous process / previous WC WIP |
| Query helper Ex | `ProductionPlan/HelperEx/ProdPlanHelperEx.cs` | Direct-stock/warehouse/as-of query |
| Production validation | `ERPClasses/BL/ProductionBL.cs` | BOM validation, WIP qty validation, process ordering |
| WIP access | `ERPClasses/BL/WIPItemBalLocBL.cs` | WIP lot reads |
| WO BOM access | `ERPClasses/BL/PrSchBOMBL.cs` | Work Order BOM reads |
| Daily prod access | `ERPClasses/BL/PrSchDailyProdBL.cs` | Daily production reads |
| FG posting | `ProductionPlan/HelperEx/DailyFGPostHelper.cs` | Separate FG receipt into inventory |

## 3.2 Duplicate `ERP/ProdPlan` versus `ProductionPlan/ProdPlan`

**CONFIRMED FROM CODE**

`ERP/ProdPlan/DailyPrdBatchAll.aspx` exists and declares:

```text
CodeBehind="DailyPrdBatchAll.aspx.cs"
Inherits="ProductionPlan.ProdPlan.DailyPrdBatchAll"
```

but a corresponding:

```text
ERP/ProdPlan/DailyPrdBatchAll.aspx.cs
```

does not exist.

The real inspected code-behind is:

```text
ProductionPlan/ProdPlan/DailyPrdBatchAll.aspx.cs
```

### Conclusion

The `ERP/ProdPlan` tree contains deployment/web markup copies and references the `ProductionPlan` implementation. It should **not** be treated as an independent second implementation.

---

# 4. Complete Database Object Map

## 4.1 Core Work Order snapshot

| Object | Role |
|---|---|
| `PrSchMas` | Work Order / production schedule header |
| `PrSchWCenter` | Work Order Work Centre snapshot |
| `PrSchProcess` | Work Order process snapshot |
| `PrSchBOM` | Work Order process BOM snapshot |
| `PrSchMachine` | Work Order machine snapshot |
| `PrSchLabour` | Work Order labour snapshot where used |

## 4.2 Daily execution

| Object | Role |
|---|---|
| `PrSchDailyPlan` | Planned daily production input used by mobile/auto helper variants |
| `PrSchDailyProcess` | Cumulative process output/progress aggregate |
| `PrSchDailyProd` | Detailed posted Daily Production history |

## 4.3 Production balance

| Object | Role |
|---|---|
| `WIPItemBalLoc` | WIP/production balance by WO/WC/process/item/lot |
| `WIPItemBal` | Summary-level WIP table in broader production posting flows |

## 4.4 Inventory

| Object | Role |
|---|---|
| `IvTrxBatch` | Staging/document header |
| `IvTrxBatchDetail` | Staging/document lines, also carries legacy production metadata |
| `IvBalLoc` | Inventory balance by warehouse/location/lot |
| `IvBalance` | Inventory summary balance |
| `IvTrxHistory` | Posted inventory movement ledger |
| `IvTrxHistoryRollback` | Inventory rollback history |
| `IvMas` | Item master |
| `IvType` | Inventory type / stock behavior |

## 4.5 Important views

| View | Role |
|---|---|
| `vprd_DailyProdList` | Searchable Daily Production target list |
| `vprd_DailyProdOutput` | Daily Production output/query helper source |
| `vgridProdFGWithoutDailyProd` | FG posting view used by later legacy FG logic |

## 4.6 Stored procedures

**NOT VERIFIED**

The inspected Daily Production core path is dominated by:

- raw SQL;
- ADO `DataTable`;
- adapters (`CAdapter`, `CAdapterProd`);
- generic posting helpers.

No stored procedure was found that replaces the central Daily Production calculation/posting flow. Therefore no stored procedure should be presented as a core Daily Production authority without further DB-only inspection.

---

# 5. Daily Production UI Flow

## 5.1 Search

The main page searches:

```text
vprd_DailyProdList
```

using combinations of:

```text
Work Order
Work Centre
Work Centre output item
Process
Machine
status / include closed
```

Open production is filtered so:

```text
WStatus NOT IN ('Closed','Completed')
```

when closed records are not requested.

## 5.2 Entry target

The production target contains:

```text
ScheCode       = Work Order
WCCode         = Work Centre
WCICode        = Work Centre output item
NextProcess    = process
MachineCode
```

## 5.3 Input and output fields

The staged line carries both source and destination lineage.

### Source/input side

```text
PreWCenter
PreProcess
PreICode
PreLotNo
PreRevNo
FrStdQty
FrStdUom
PreTrxType
```

### Output side

```text
WCProcessCode
ToLot
LotRevNo
ToStdQty
ToStdUom
ScrapStdQty
reject fields
FinalProcess
```

This is strong proof that Daily Production is a **material/WIP transformation transaction**, not merely an output journal.

---

# 6. Work Order → Daily Production Relationship

## 6.1 Frozen schedule structure

**CONFIRMED FROM CODE**

Daily Production works against:

```text
PrSchMas
  └─ PrSchWCenter
       └─ PrSchProcess
            ├─ PrSchBOM
            └─ PrSchMachine
```

It does not use current Product Definition BOM as its primary execution source.

## 6.2 Process BOM ownership

A process calculation calls:

```text
PrSchBOMBL.GetBOM(
    workOrder,
    relNo,
    workCentre,
    process)
```

and selects:

```text
BomDefault = 1
```

Therefore BOM consumption is **process-scoped**.

It does not indiscriminately consume every BOM line from the whole Work Centre.

---

# 7. Daily Output Calculation

## 7.1 Actual production quantities

Legacy helper variants read:

```text
QtyAct
QtyScrap
QtyReject
```

from daily plan/entry data.

The primary quantities are:

```text
Good Output
Scrap
Reject
```

with:

```text
Total execution basis =
    Good + Scrap + Reject
```

## 7.2 Cumulative process output

`DailyProdPostHelper.PostWithSchedule(...)` creates or updates:

```text
PrSchDailyProcess.TotalQty
```

using:

```text
TotalQty += ToStdQty
```

Thus repeated Daily Production transactions accumulate.

## 7.3 Multiple entries per day

**CONFIRMED FROM CODE**

`PrSchDailyProd` uses line numbers/batch transaction lines, and the posting code always adds a detailed production row. There is no “one process per day only” constraint in the traced logic.

Therefore multiple Daily Production postings for the same:

```text
WO + WC + Process + date
```

are supported.

---

# 8. BOM Requirement Calculation

## 8.1 Legacy exact formula

For each default Work Order BOM item:

```text
BOM Qty = PrSchBOM.StdQty
Production basis = output + scrap + reject
Centre basis = PrSchWCenter.ScheQty

Total Material Required
  = BOM Qty × Production basis ÷ Centre basis
```

Rounded to 4 decimals.

Good/scrap/reject are then allocated separately.

## 8.2 Catch weight

When:

```text
AdPara.UseWeight = true
```

the helper uses:

```text
DefCatchWt
```

to calculate weight.

If weight mode is disabled, catch-weight consumption is zeroed.

## 8.3 Material lots

When multiple candidate rows exist, the helper can distribute one material requirement over multiple source rows.

The result is one or more staged transaction detail rows.

### Important limitation

**LEGACY BEHAVIOR — SHOULD NOT PORT**

The standard WIP query order is not a robust business allocation policy. It often orders by identifiers such as:

```text
ICode
WCCode
ProcessCode
LotNo
RevNo
ScheCode
WCICode
```

not by an explicit FIFO/FEFO date rule.

The new ERP should use its existing deterministic allocation rules.

---

# 9. Material Issue Integration

## 9.1 Standard mode — Issue to Production first

**CONFIRMED FROM CODE**

The standard `DailyPodOutPutHelper` obtains material from:

```text
WIPItemBalLoc
```

through `ProdPlanHelper.GetAllItemSql(...)`.

One branch explicitly reads:

```text
ProcessCode LIKE '%STOCK%'
```

and BOM-valid items from `PrSchBOM`.

This is production-side material created by Issue to Production.

So standard mode is:

```text
Inventory
   ↓ IP
WIP / production material
   ↓ WI Daily Production
new process output WIP
```

## 9.2 No-Issue mode — direct warehouse backflush

**CONFIRMED FROM CODE**

`DailyPodOutPutNoIssueHelper` loads normal inventory through:

```text
ProdPlanHelperEx.GetAllItemSqlStock_ByWarehouse(...)
```

against:

```text
IvBalLoc
```

and the No-Issue post helper calls:

```text
CPosting.PostInventoryTransaction("IP", ...)
```

as part of Daily Production posting.

Thus this mode is:

```text
Inventory
   ↓ automatic/direct IP during output
Daily Production
   ↓
WIP output
```

## 9.3 New ERP mapping

The new ERP already carries:

```text
ProductionWorkOrderMaterial.IssueMethod
```

including:

```text
MANUAL
BACKFLUSH
PICK_LIST
```

and currently blocks manual issue for BACKFLUSH materials with the explicit message:

```text
Backflush — issued automatically by production output.
```

That is the correct architectural hook for Daily Production.

---

# 10. Raw Material Consumption Flow

## 10.1 MANUAL material

**RECOMMENDED NEW DESIGN**

For:

```text
IssueMethod = MANUAL
```

Daily Production must not independently reduce normal inventory again.

Instead:

```text
Material Issue
     ↓
ProductionMaterialMovement = ISSUE
     ↓
production-available issued quantity
     ↓
Daily Production
     ↓
ProductionMaterialMovement = CONSUME
```

The execution service should verify:

```text
available issued qty
 = effective issues
 - returns
 - previous consumption
```

and consume only that available quantity.

## 10.2 BACKFLUSH material

For:

```text
IssueMethod = BACKFLUSH
SupplySource = PURCHASED / EXTERNAL_SUPPLY
```

Daily Production should:

1. calculate required material for the reported output;
2. allocate eligible inventory as of the production date;
3. lock and revalidate the stock rows;
4. post inventory stock-out;
5. create `CONSUME` production movement facts;
6. associate the inventory transaction to the Daily Production posting;
7. commit everything atomically.

This cleanly reproduces the legacy No-Issue business mode without duplicating its technical implementation.

---

# 11. Production Balance / WIP Lot Flow

## 11.1 Legacy WIP creation

`DailyProdPostHelper.PostWIPWithSchedule(...)` locates an output balance by:

```text
ICode
WCCode
ProcessCode
ToLot
RevNo
ScheCode
RelNo
```

If no row exists, it creates `WIPItemBalLoc`.

If it exists, it increments:

```text
StdQty
WtQty
```

## 11.2 Legacy WIP consumption

If the detail has source lineage (`PreICode`, `PreLotNo`, etc.), the poster reduces the corresponding source `WIPItemBalLoc`.

Negative WIP is rejected.

## 11.3 Multiple source lots

A single production output can be satisfied by multiple source lots because the helper can create multiple detail rows for the same production output.

Example:

```text
Need WIP001 = 70

LOT-A balance = 30
LOT-B balance = 50

Consumption:
LOT-A = 30
LOT-B = 40

Remaining:
LOT-A = 0
LOT-B = 10
```

## 11.4 Work Order restriction

The standard helper filters by the current `ScheCode`, therefore normal WIP consumption is same-Work-Order.

The permissive “ANY” variants relax those constraints.

### Recommendation

Default new behavior should be:

```text
INTERNAL_ROUTE_WIP
→ same Work Order
→ exact producing route step
→ eligible output lots
```

Cross-Work-Order WIP should not happen implicitly. If ever supported, it should be a separately authorized transfer/reclassification transaction.

---

# 12. Work Centre / Process / Machine Logic

## 12.1 Process sequencing

`ProdPlanHelper.GetPrevousProcess(...)` resolves the previous process by sequence.

The main helper has newer checks that prevent current-process output when the previous process has no posted good quantity.

## 12.2 Prior Work Centre hand-off

For a previous Work Centre, `ProdPlanHelper.GetAllItemSql(...)` only exposes output from:

```text
PrSchProcess.FinalProcess = 1
```

when that output is a BOM input of the current process.

This means FinalProcess is the **Work Centre hand-off boundary**.

## 12.3 Planned machine vs actual machine

The old code contains a legacy distinction:

- `MachineCode` in production data represents the scheduled/selected machine in several flows;
- the page stores the actual working machine in `IvTrxBatchDetail.PO_NO` with an explicit code comment.

**LEGACY BEHAVIOR — SHOULD NOT PORT**

Reusing `PO_NO` for machine identity is technical debt.

### New design

Daily Production should carry explicit fields:

```text
PlannedMachineCode
ActualMachineCode
OperatorCode / OperatorId
ShiftCode
StartDateTime
EndDateTime
```

Actual machine may default from the Work Order's selected machine but must be independently recorded.

---

# 13. Partial Production / Cumulative Quantity Logic

## 13.1 Partial output

Legacy Daily Production is inherently partial.

Example:

```text
WO Qty = 1,000
Day 1 = 200
Day 2 = 300
Day 3 = 250
Day 4 = 250
```

Each posting creates a separate `PrSchDailyProd` fact and cumulative quantity is updated in `PrSchDailyProcess`.

## 13.2 Current Blazor aggregate fields

The new ERP already has:

```text
ProductionWorkOrder:
  GoodQty
  ScrapQty
  RejectQty
  HoldQty
  ApprovedVarianceQty
  RemainingQty

ProductionWorkOrderOperation:
  InputQty
  ProcessedQty
  GoodQty
  ScrapQty
  RejectQty
  HoldQty
  ReworkQty
  TransferredQty
  RemainingQty
```

and helpers such as:

```text
OpenProductionQty
OperationProcessedQty
OperationRemainingQty
AvailableForNextOperation
```

Therefore these fields should be **projections rebuilt from immutable production execution facts**, not manually edited counters.

---

# 14. Inventory Transaction Flow

## 14.1 Standard Daily Production

Normal standard-mode WI posting is primarily:

```text
production WIP → production WIP
```

through `WIPItemBalLoc`.

The raw inventory has already moved through `IP`.

## 14.2 No-Issue Daily Production

No-Issue mode also invokes:

```text
CPosting.PostInventoryTransaction("IP", ...)
```

to remove warehouse material during Daily Production.

## 14.3 Finished Goods

Finished Goods posting uses:

```text
FGS
```

and normal inventory posting.

## 14.4 New design transaction classes

Recommended explicit new command/document concepts:

```text
PRODUCTION_OUTPUT
PRODUCTION_OUTPUT_REVERSAL
WIP_TRANSFER / WIP_CONSUME
FG_RECEIPT
FG_RECEIPT_REVERSAL
```

The exact inventory `TrxType` may follow the existing project's naming conventions, but production-side document identity must not be hidden inside generic fields.

---

# 15. Warehouse / Location / Lot Rules

## 15.1 Legacy standard issued material

Standard production-side material is selected from WIP and is tied to Work Order / process / lot.

## 15.2 Legacy No-Issue source warehouse

`GetAllItemSqlStock_ByWarehouse(...)` explicitly filters:

```text
IvBalLoc.WHCode = requested material warehouse
```

and Work Order BOM:

```text
ScheCode
WCCode
ProcessCode
BomDefault = 1
```

## 15.3 New ERP already enforces stronger rules

`ProductionMaterialAllocationService` and Material Issue posting validate:

```text
Company
Branch
Item
Work Order material warehouse
optional Work Order material location
item active
stock controlled
item status
UOM
lot active
expiry date
as-of stock
```

Candidate order is effectively:

```text
FEFO for expiring lot-controlled stock
then stock date
then lot number
then balance id
```

Daily Production should reuse the same policy and lock/revalidation infrastructure.

---

# 16. UOM / Quantity / Rounding Rules

## 16.1 Legacy

Legacy code commonly uses:

```text
double
Math.Round(...)
CCommon.RoundingToDouble(..., 4)
```

Catch-weight can create a second quantity dimension.

## 16.2 New ERP

The new ERP uses:

```text
decimal
IvQty.Round
IUomConversionService
RequiredQty
RequiredBaseQty
ConversionFactorToBase
```

This is superior.

### Rule

**RECOMMENDED NEW DESIGN**

Daily Production must use decimal quantities end-to-end.

Do not copy:

```text
double-based arithmetic
legacy implicit UOM assumptions
overloaded weight fields
```

All material and output calculations should operate on:

```text
business UOM
base stock UOM
explicit conversion factor
4-decimal quantity policy
```

with no hidden conversions.

---

# 17. Save Transaction Sequence

## 17.1 Legacy manual UI sequence

Actual legacy sequence is broadly:

```text
User chooses production targets
    ↓
DailyProdHelper converts selection to staged detail rows
    ↓
Validate WIP input qty
    ↓
Create IvTrxBatch header(s), TrxType = WI
    ↓
Start SQL transaction
    ↓
Save numbering
Save IvTrxBatch
Save IvTrxBatchDetail
Save numbering-by-date
    ↓
Commit draft
    ↓
if user requested posting
    ↓
ProdNeedPostHelper.PostDaily(...)
    ↓
DailyProdPostHelper
    ↓
separate posting transaction
```

### Important transaction boundary

**CONFIRMED FROM CODE**

The batch draft save is atomic.

The posting is atomic.

But:

```text
save + post
```

is not one single database transaction in the main page flow.

Therefore a failed post can leave a valid `NEW` batch to be retried.

## 17.2 No-Issue API/helper sequence

The No-Issue helper is more tightly integrated and runs:

```text
Daily production facts
WIP update
raw inventory IP posting
history update
batch status
```

through one helper transaction.

---

# 18. Edit / Delete / Cancel / Reverse Logic

## 18.1 Posted edit protection

The main page checks:

```text
BatchStatus == POSTED
```

and blocks ordinary save/edit.

## 18.2 Daily Production rollback

`DailyProdPostHelper.StartRollBack(...)`:

1. opens posted batch/WIP/daily state;
2. removes `PrSchDailyProd` rows belonging to the batch;
3. reverses output WIP;
4. restores source WIP;
5. reverses process cumulative qty/status;
6. sets Work Order back to in-progress as required;
7. updates batch state;
8. commits all changes in one SQL transaction.

## 18.3 Downstream-used output protection

When rollback tries to remove the produced WIP quantity, the helper calculates the remaining lot balance.

If current output balance is less than the quantity originally produced, rollback fails with messages such as:

```text
Insufficient qty to rollback
```

Thus downstream consumption prevents full rollback indirectly through balance sufficiency.

### New rule

The new ERP should improve this using explicit lineage:

```text
ProductionOutputMovement
      ↓ OriginalMovementId / SourceOutputLot
ProductionWipConsumptionMovement
```

Rollback should explicitly query dependent later movements and provide a precise blocking reason.

This is already the pattern used in the new Material Issue rollback code.

---

# 19. Work Order / Process Completion Logic

## 19.1 Legacy Work Order status

The poster inspects the last process/output of the final Work Centre.

When final cumulative output reaches scheduled quantity, it changes:

```text
PrSchMas.Status = COMPLETED
```

otherwise:

```text
IN PROGRESS
```

## 19.2 Legacy process completion field weakness

The current `PrSchDailyProcess.Completed` logic contains code where even when:

```text
TotalQty >= WCScheQty
```

the field is still assigned `0` in active code, with older completion logic commented.

**LEGACY BEHAVIOR — SHOULD NOT PORT**

Do not use this field behavior as the new business definition of process completion.

## 19.3 Recommended completion rule

Use immutable production facts and calculate:

```text
operation processed
operation good output
operation transferable output
route-step output
WO final accepted output
```

Completion must be an explicit lifecycle command with validation, not a side effect of one arbitrary counter.

---

# 20. Concurrency and Data Integrity Rules

## 20.1 Legacy strengths

- draft writes use SQL transaction;
- post uses SQL transaction;
- rollback uses SQL transaction;
- quantities are re-read in some validations;
- posted documents are protected from normal edit;
- WIP negative balance is checked.

## 20.2 Legacy weaknesses

**CONFIRMED / INFERRED FROM CODE**

Several validations occur before row-level locking.

Examples:

```text
ValidateInputQtyEx(...)
WIP lookup
balance check
```

can occur before the final posting transaction has locked the same lot.

Two users can therefore race.

Legacy code also relies heavily on:

```text
DataTable
raw SQL
application sequencing
generic mutable batch detail fields
```

rather than immutable execution facts plus deterministic DB locking.

## 20.3 New ERP standard

The new Material Issue implementation already demonstrates the correct pattern:

```text
BeginTransaction
    ↓
UPDLOCK + HOLDLOCK Work Order
    ↓
UPDLOCK + HOLDLOCK material rows
    ↓
lock stock slices in deterministic order
    ↓
recalculate as-of availability
    ↓
revalidate warehouse/location/UOM/lot/tolerance
    ↓
post inventory within same DbContext transaction
    ↓
write immutable production movements
    ↓
update projections
    ↓
commit
```

Daily Production should use exactly this pattern.

---

# 21. Future Stock / Transaction Date

## 21.1 Legacy

The improved No-Issue SQL explicitly appends:

```text
b.TransDate <= production transaction date
```

with the comment:

```text
to filter out future stock
```

So the legacy code recognizes this rule in at least the direct-stock path.

It is not uniformly proven across every old WIP helper.

## 21.2 New ERP

The new Inventory subsystem is substantially stronger.

`InventoryAsOfStockService` reconstructs stock as of a date by reversing later inventory movements.

Therefore:

```text
Usable = min(Current Qty, As-Of Qty)
```

conceptually.

`IvStockDateRules` also rejects stock whose stock date is after the document date.

## 21.3 Required Daily Production rule

For Production Date:

```text
1-Oct-2026
```

Daily Production must not consume:

```text
inventory created 2-Oct-2026
or
WIP output created 2-Oct-2026
```

The same concept must apply to **both inventory and WIP**.

Recommended new WIP availability should be as-of-date aware, not only:

```text
Current WIP Balance > 0
```

---

# 22. Legacy Helper Variants — Classification

| Helper | Meaning | Classification |
|---|---|---|
| `DailyPodOutPutHelper` | Standard issued/WIP material flow | **CORE BUSINESS RULE** |
| `DailyPodOutPutNoIssueHelper` | Direct warehouse consumption/backflush | **OPTIONAL BUSINESS RULE** |
| `DailyPodOutPutHelperAny` | More permissive WIP source selection | **OPTIONAL / HIGH-RISK LEGACY RULE** |
| `DailyPodOutPutHelperAnyEx` | Explicitly bypasses schedule/WIP conflicts for “ANY” behavior | **CUSTOMER-SPECIFIC / SHOULD NOT BE DEFAULT** |
| JONG helper paths | Special production/batch behavior | **LEGACY/CUSTOMER VARIANT — VERIFY BEFORE PORTING** |
| `PO_NO` used for actual machine | Field overloading | **LEGACY TECHNICAL WORKAROUND** |
| `Cost` / `UnitPrice` used for production scrap/reject in old posting | Field overloading | **LEGACY TECHNICAL WORKAROUND** |
| Web Forms Session DataTables | UI persistence | **OBSOLETE / SHOULD NOT PORT** |

---

# 23. Core Rules That Must Be Preserved

The following are the proven domain rules worth preserving.

## 23.1 Work Order authority

```text
Execution uses the frozen Work Order structure.
```

## 23.2 Exact operation material ownership

```text
A production process consumes only its own BOM/material requirements.
```

## 23.3 Partial production

```text
One WO/process can have many output postings.
```

## 23.4 Material proportionality

```text
Material consumption is proportional to actual processed output,
including defined scrap/reject behavior.
```

## 23.5 Prior-process capacity

```text
A downstream operation cannot legitimately process more transferable
WIP than upstream production has made available.
```

## 23.6 Lot lineage

```text
Every WIP consumption must retain its source lot.
Every WIP output must retain its output lot.
```

## 23.7 Same-Work-Order internal WIP by default

```text
Internal-route WIP belongs to its Work Order and producer route step.
```

## 23.8 FinalProcess / route output hand-off

```text
A Work Centre's hand-off output is exposed after the required final operation.
```

## 23.9 Reversible posting

```text
Posted production must be reversible only when downstream dependencies permit.
```

## 23.10 Atomic execution

```text
All stock/WIP/progress facts for one posting either commit together or do not commit.
```

---

# 24. Old ERP → New ERP Mapping

| Old ERP concept | Old object/class | New existing equivalent | Gap / required change |
|---|---|---|---|
| Work Order | `PrSchMas` | `ProductionWorkOrder` | Existing and stronger |
| Work Centre snapshot | `PrSchWCenter` | `ProductionWorkOrderRouteStep` | Existing |
| Process snapshot | `PrSchProcess` | `ProductionWorkOrderOperation` | Existing |
| WO BOM | `PrSchBOM` | `ProductionWorkOrderMaterial` | Existing and stronger |
| Machine | `PrSchMachine` | `ProductionWorkOrderMachine` | Existing |
| Labour | `PrSchLabour` | `ProductionWorkOrderLabour` | Existing |
| Material issue | IP / Issue-to-Production helpers | `ProductionMaterialIssueService` | Implemented |
| Material movement lineage | scattered legacy fields | `ProductionMaterialMovement` | Existing; extend for production consumption |
| Posting identity | batch/ref fields | `ProductionPostingLink` | Existing; extend command types |
| Daily Production fact | `PrSchDailyProd` | legacy entity exists | Need new execution aggregate/source of truth |
| Process cumulative | `PrSchDailyProcess` | operation projection fields | Rebuild from output facts |
| Production balance | `WIPItemBalLoc` | legacy entity exists | Need safe new WIP movement service/ledger |
| Production output lot | `WIPItemBalLoc` lot | no modern production-output lot aggregate yet | Add explicit WIP/output movement model |
| Raw inventory | `IvBalLoc` | `IvBalLoc` | Existing |
| Inventory history | `IvTrxHistory` | `IvTrxHistory` | Existing |
| Future stock rule | partial legacy filters | `InventoryAsOfStockService` + chronology rules | Existing for inventory; extend concept to WIP |
| FG posting | `DailyFGPostHelper`, `FGS` | inventory posting foundation exists | Need production FG receipt service |
| Daily Production rollback | `DailyProdPostHelper.StartRollBack` | Material Issue rollback pattern | Implement output rollback with dependency checks |

---

# 25. Gaps in the Current Blazor Production Module

## 25.1 No modern Daily Production source transaction

There is currently no modern service/entity set comparable to:

```text
ProductionOutputHeader
ProductionOutputLine
ProductionOutputMaterialConsumption
ProductionWipMovement
```

The mapped legacy `PrSchDailyProd` table exists, but using it directly as the new domain aggregate would carry too much legacy field ambiguity.

## 25.2 WIP balance exists, but modern WIP ledger does not

`WipItemBalLoc` exists and has RowVersion, but it remains a balance table.

A modern execution model also needs immutable movement lineage.

Recommended:

```text
ProductionWipMovement
```

or a generalized production movement entity containing:

```text
WorkOrderId
RouteStepId
OperationId
MovementType
ItemCode
Qty / BaseQty / UOM
Lot identity
SourceWipLotId / source movement
OutputWipLotId
ProductionOutputPostingId
MovementDate
Cost
OriginalMovementId
```

## 25.3 Route output type is not frozen into Work Order route step

Product Definition contains:

```text
PrBomRouteStep.OutputType
  WIP_STOCKED
  WIP_NONSTOCK
  FINISHED_GOODS
```

but `ProductionWorkOrderRouteStep` currently does not preserve `OutputType`.

This is a critical gap for Daily Production because execution must know whether route output should be:

```text
stocked WIP
non-stock transfer quantity
finished goods
```

### Required change

Add frozen:

```text
OutputType
```

to the Work Order route step snapshot and snapshot hash.

## 25.4 Yield/loss semantics still need a final contract

Product Definition has:

```text
YieldPercent
SetupLossQty
OperationLossQty
```

but current route step snapshot/calculator does not fully execute yield semantics.

Daily Production must not invent a second loss model. The quantity contract must be finalized before execution is enabled.

## 25.5 No new production output posting service

Need:

```text
IProductionOutputService
ProductionOutputService
```

with draft/read/post/rollback lifecycle similar to Material Issue.

## 25.6 No modern FG receipt service tied to production output

A dedicated production receipt command is needed.

Do not reuse legacy `DailyFGPostHelper`.

## 25.7 Execution gate still closed

Current code:

```text
ProductionExecutionGate.IsOpen => false
```

This should remain closed for the overall output/WIP/FG execution feature until the required contracts and rollback paths are implemented.

---

# 26. Recommended New Daily Production Architecture

## 26.1 Domain model

### A. Production Output document

Recommended aggregate:

```text
ProductionOutput
    Uid
    CompanyCode
    BranchCode
    DocumentNo
    PostingRequestId
    WorkOrderId
    RouteStepId
    OperationId
    ProductionDateTime
    ShiftCode
    PlannedMachineId/Code
    ActualMachineId/Code
    Operator
    GoodQty
    ScrapQty
    RejectQty
    HoldQty
    ReworkQty
    OutputUom
    OutputItemCode
    OutputType
    OutputLotNo / LotId
    Warehouse / Location where applicable
    Status
    SnapshotRevision / SnapshotHash
    RowVersion
```

### B. Material consumption rows

```text
ProductionOutputConsumption
    ProductionOutputId
    WorkOrderMaterialId
    SourceType
       ISSUED_MATERIAL
       INVENTORY_BACKFLUSH
       INTERNAL_ROUTE_WIP
    Qty
    BaseQty
    Uom
    InventoryBalLocId?
    MaterialIssueMovementId?
    SourceWipLotId?
    SourceMovementId?
```

### C. WIP movements

Recommended immutable facts:

```text
ProductionWipMovement
    WorkOrderId
    RouteStepId
    OperationId
    OutputId
    MovementType
       PRODUCE
       CONSUME
       REVERSAL
       TRANSFER
       ADJUSTMENT
    ItemCode
    LotNo
    Qty
    Uom
    MovementDate
    OriginalMovementId
    SourceLot/movement
    Cost
```

The balance table is then a projection.

---

# 27. Recommended Production Output Calculation

## 27.1 Good/scrap/reject basis

For each output command:

```text
ProcessedQty =
    GoodQty
  + ScrapQty
  + RejectQty
  + HoldQty
```

Rework semantics should be finalized separately; avoid counting rework twice.

## 27.2 Material proportionality

Use the frozen Work Order material standard.

For a material whose frozen requirement is derived against operation planned output:

```text
ConsumptionQty
    = ProcessedOutputBasis
      × ComponentQtyPerParent
      ÷ BomOutputQty
      × (1 + ScrapPercent / 100)
```

with UOM conversions performed through the existing conversion service.

However, if `ScrapPercent` already models engineering scrap and user-entered production scrap also increases processed basis, the business must explicitly approve whether both apply together. The implementation must avoid accidental double-scrap.

Recommended contract:

- engineering material scrap percent = expected additional material loss;
- output scrap/reject = actual units processed but not accepted;
- both may apply if that is the intended BOM semantics.

This must be clearly documented in the UI.

---

# 28. Recommended Source Resolution Strategy

For each frozen Work Order material:

## 28.1 MANUAL + purchased/external

```text
Source = prior ProductionMaterialMovement ISSUE facts
Available =
  ISSUE
- ISSUE_REVERSAL
- RETURN
- CONSUME
```

Consumption writes:

```text
ProductionMaterialMovement = CONSUME
OriginalMovementId = source issue movement where practical
```

If several issue lots satisfy consumption, create multiple consume facts.

## 28.2 BACKFLUSH + purchased/external

```text
Source = normal inventory
```

Use existing:

```text
ProductionMaterialAllocationService
InventoryAsOfStockService
IvInventoryPostingService
```

to allocate/lock/post.

## 28.3 INTERNAL_ROUTE_WIP

```text
Source = WIP produced by material.ProducingRouteStep
within same Work Order
```

Eligibility:

```text
Produced Date <= Production Date
Available Qty > 0
correct item
correct producer route step
same Work Order
not reversed
not downstream-exhausted
```

Recommended allocation:

```text
FIFO by production movement date/time
then lot no
then movement id
```

unless the operator manually selects lots.

---

# 29. Recommended Output Creation Rules

Use the Work Order route-step snapshot:

```text
OutputItemCode
OutputUom
OutputType
```

### `WIP_STOCKED`

Create/increment WIP lot balance plus immutable WIP PRODUCE fact.

### `WIP_NONSTOCK`

Do not create stock-like WIP balance. Record transferable operation/route quantity and movement lineage.

### `FINISHED_GOODS`

Create the production output fact first.

Then either:

1. require a separate **Receive Finished Goods** command; or
2. allow “Post & Receive FG” as one composite command.

If composite, both production and inventory posting must commit in the **same DB transaction**.

---

# 30. Recommended Future-Stock / WIP Chronology Rules

## 30.1 Inventory

Reuse existing as-of service.

## 30.2 WIP

Add WIP equivalent:

```text
IProductionWipAsOfService
```

Usable WIP for a production date should be calculated from immutable WIP movements as of that date.

The posting service must reject:

```text
source WIP movement date > production date
```

## 30.3 Rollback chronology

Block rollback when:

```text
a later consumption references the produced output
a later FG receipt depends on it
a later WIP transfer consumes it
a later production adjustment depends on it
```

This should be explicit dependency validation, not only a current-balance comparison.

---

# 31. Recommended Save/Post Lifecycle

```text
NEW DRAFT
   ↓
validate basic document
   ↓
save draft
   ↓
POST
   ↓
begin transaction
   ↓
lock ProductionPostingLink/idempotency request
   ↓
lock Work Order
   ↓
verify status RELEASED / IN_PROGRESS
   ↓
verify SnapshotRevision + SnapshotHash
   ↓
lock operation
   ↓
validate output qty / overproduction
   ↓
load frozen operation materials
   ↓
calculate required consumption
   ↓
for MANUAL:
    lock/revalidate issued material availability
for BACKFLUSH:
    lock inventory balances + as-of validation
for INTERNAL_ROUTE_WIP:
    lock WIP lots + as-of validation
   ↓
create immutable material CONSUME facts
   ↓
post any inventory backflush
   ↓
create production output fact
   ↓
create WIP PRODUCE fact / FG receipt as applicable
   ↓
update WIP projection
   ↓
rebuild operation / route / WO projections
   ↓
write audit event
   ↓
mark posting link SUCCEEDED
   ↓
commit
```

No stock or production projection may be updated outside this transaction.

---

# 32. Recommended Rollback Sequence

```text
User requests rollback
   ↓
find original ProductionPostingLink
   ↓
idempotency check
   ↓
begin transaction
   ↓
lock Work Order + output + source facts
   ↓
check downstream dependencies
   │
   ├─ dependent WIP consumption exists → BLOCK
   ├─ dependent FG receipt exists → BLOCK / reverse dependent first
   └─ no dependency
   ↓
reverse produced WIP/output
   ↓
reverse material CONSUME facts
   ↓
if backflush:
    rollback inventory stock-out
   ↓
restore WIP source balances
   ↓
rebuild operation/WO projections
   ↓
write reversal facts and audit
   ↓
mark original posting REVERSED
   ↓
commit
```

Never delete posted immutable facts.

---

# 33. Concrete Implementation Plan for `mokth/net10projectTemplate` / `production`

## Phase 0 — Contract hardening before coding Daily Production

### 0.1 Freeze route output type into Work Order

Modify:

```text
ErpWeb.Model/Entities/Production/ProductionWorkOrderRouteStep.cs
ErpWeb.Core/Production/WorkOrderSnapshotBuilder.cs
snapshot hashing/readiness
EF configuration + migration
```

Add:

```text
OutputType
```

copied from `PrBomRouteStep.OutputType`.

### 0.2 Finalize quantity/loss contract

Document and lock down semantics for:

```text
YieldPercent
SetupLossQty
OperationLossQty
BOM ScrapPercent
reported ScrapQty
reported RejectQty
HoldQty
ReworkQty
```

Do not implement Daily Production until there is exactly one formula for every projection.

### 0.3 Define overproduction policy

Recommended:

```text
Max Output Allowed
 = planned operation output
   × (1 + configured output tolerance %)
```

or use an explicit approved-variance quantity.

Do not silently reuse material tolerance as output tolerance.

---

## Phase 1 — Production Output persistence

Add modern entities:

```text
ProductionOutput
ProductionOutputConsumption
ProductionWipMovement
```

Do **not** make `PrSchDailyProd` the authoritative new aggregate.

`PrSchDailyProd` can remain:

- legacy compatibility;
- reporting bridge;
- migration source;
- optional projection if old reports still require it.

Add:

```text
ProductionPostingCommandTypes.OutputPost
ProductionPostingCommandTypes.OutputRollback
ProductionDocumentTypes.ProductionOutput
ProductionAuditEventTypes.OutputPosted
ProductionAuditEventTypes.OutputRolledBack
```

---

## Phase 2 — WIP service

Create:

```text
IProductionWipService
ProductionWipService
IProductionWipAsOfService
ProductionWipAsOfService
```

Responsibilities:

```text
candidate WIP lots
as-of availability
manual lot selection
auto FIFO allocation
row locking
consume
produce
reverse
rebuild balance projection
```

Use deterministic SQL locks similar to Material Issue.

If the existing `WIPItemBalLoc` must remain for compatibility, update it only as a **projection** of immutable WIP movement facts.

---

## Phase 3 — Production material consumption service

Create execution calculator/service that resolves each frozen material by:

```text
IssueMethod
SupplySource
```

Matrix:

| Issue Method | Supply Source | Daily Production behavior |
|---|---|---|
| MANUAL | PURCHASED / EXTERNAL | consume from previously issued production movements |
| BACKFLUSH | PURCHASED / EXTERNAL | consume normal inventory atomically |
| any | INTERNAL_ROUTE_WIP | consume eligible Work Order WIP output |
| PICK_LIST | purchased/external | only enable once Pick List execution exists |
| separate product definition | separate supply | validate material has been received/issued through its defined flow |

---

## Phase 4 — Production Output posting service

Create:

```text
IProductionOutputService
ProductionOutputService.cs
ProductionOutputService.Draft.cs
ProductionOutputService.Posting.cs
ProductionOutputService.Rollback.cs
ProductionOutputService.Read.cs
ProductionOutputService.Search.cs
```

Follow the existing `ProductionMaterialIssueService` organization and patterns.

### Required posting protections

- `ProductionPostingLink` idempotency;
- Work Order `UPDLOCK, HOLDLOCK`;
- operation row lock;
- material rows locked in ID order;
- WIP lots locked in deterministic key order;
- inventory BalLoc locks in deterministic slice order;
- period-open check where inventory is affected;
- snapshot revision/hash validation;
- RowVersion/concurrency exception handling;
- retry with same posting request ID.

---

## Phase 5 — Projection rebuild

After each post/rollback, rebuild from immutable facts:

### Operation

```text
InputQty
ProcessedQty
GoodQty
ScrapQty
RejectQty
HoldQty
ReworkQty
TransferredQty
RemainingQty
```

### Work Order

```text
GoodQty
ScrapQty
RejectQty
HoldQty
RemainingQty
Status
```

Do not increment/decrement these ad hoc in many code paths.

Prefer a single:

```text
ProductionExecutionProjectionService
```

that calculates them from source transactions.

---

## Phase 6 — UI

Follow the existing Inventory transaction UI pattern.

Recommended screens:

```text
PrDailyProductionList.razor
PrDailyProductionEntry.razor
```

### New entry UX

Do not start with one Work Order dropdown.

Provide search filters:

```text
Work Order
Work Centre
Process
Output Item
Raw Material
Machine
```

Search only executable:

```text
Released / In Progress Work Orders
not completed operations
not fully produced route outputs
```

Result row:

```text
WO
Product
WC
Process
Planned Machine
Output Item
Planned Qty
Produced
Remaining
Material Readiness
WIP Readiness
button: Record Output
```

### Record Output dialog/page

Header:

```text
Production Date/Time
Work Order
Work Centre
Process
Planned Machine
Actual Machine
Shift
Operator
```

Quantities:

```text
Good
Scrap
Reject
Hold
Output UOM
Remaining before
Remaining after
```

Material section:

```text
Material
Issue Method
Supply Source
Required for this output
Available Issued / WIP / Inventory
Lot allocations
Shortage
```

Output:

```text
Output Item
Output Type
Output Lot
Output Qty
Destination (if FG receipt)
```

---

# 34. Concrete Validation Checklist

The new posting command must reject at least:

1. Work Order not Released/In Progress.
2. stale snapshot hash/revision.
3. operation not in Work Order.
4. operation already fully completed beyond allowed variance.
5. negative/zero invalid output quantities.
6. processed quantity beyond allowed production tolerance.
7. invalid machine actual if machine-restricted.
8. material requirement missing or invalid UOM.
9. MANUAL material consumption greater than net issued-and-unconsumed qty.
10. BACKFLUSH inventory shortage.
11. wrong warehouse/location.
12. invalid/expired lot.
13. future inventory stock.
14. future WIP lot.
15. WIP from wrong Work Order.
16. WIP from wrong producer route step.
17. downstream process input greater than transferable upstream quantity.
18. period closed for inventory posting.
19. duplicate PostingRequestId.
20. concurrent stock/WIP mutation.
21. output lot conflicts.
22. reversal when output has downstream dependencies.
23. reversal when later inventory chronology blocks rollback.
24. completion while unresolved hold/rework/shortage exists.
25. final FG receipt inconsistent with final accepted production output.

---

# 35. Direct Answers to the 25 Critical Questions

## 1. What exactly is a Daily Production transaction in the old ERP?

A posted `WI` production execution transaction that records actual process output and transforms production-side input material/WIP into a new process/work-centre output lot while updating production history and progress.

## 2. What tables are created/updated when saved/posted?

Core tables include:

```text
IvTrxBatch
IvTrxBatchDetail
PrSchDailyProd
PrSchDailyProcess
WIPItemBalLoc
PrSchMas
```

and in No-Issue/direct stock mode also:

```text
IvBalLoc
IvBalance
IvTrxHistory
```

plus numbering/audit tables.

## 3. How is required BOM calculated from Daily Output?

Legacy:

```text
BOM Qty × (Good + Scrap + Reject) ÷ Work Centre ScheQty
```

with good/scrap/reject portions and 4-decimal rounding.

## 4. Product Definition or Work Order snapshot?

**Work Order snapshot (`PrSchBOM`).**

## 5. How are previously issued materials handled?

Standard mode reads issued production material from `WIPItemBalLoc` (STOCK/IP-style rows) and consumes that production-side balance.

## 6. What happens when Issue-to-Production is not used?

The No-Issue helper reads `IvBalLoc` by the Work Order BOM and specified warehouse, calculates required material, and posts an inventory `IP` stock-out as part of Daily Production.

## 7. How are raw materials consumed?

Either:

```text
pre-issued WIP material → WI consume
```

or:

```text
direct IvBalLoc → IP posting during Daily Production
```

depending on helper mode.

## 8. How are Production Balance/WIP materials consumed?

By reducing the exact eligible `WIPItemBalLoc` source lot(s).

## 9. How is a Production Balance lot generated?

Output is assigned `ToLot`/revision. Auto helper variants generate a `LOT` running number when blank; posted output creates/increments `WIPItemBalLoc`.

## 10. How does the next Work Centre/process consume that lot?

`ProdPlanHelper.GetAllItemSql(...)` exposes previous-process or prior-final-process WIP that matches the current Work Order and BOM.

## 11. How are multiple lots handled?

One requirement can be split over multiple source WIP/inventory rows, generating multiple detail/consumption rows.

## 12. Warehouse/location restrictions?

No-Issue mode explicitly filters the material warehouse. Standard mode follows production WIP lineage. The new ERP has stronger frozen warehouse/location enforcement.

## 13. Partial Daily Production?

Supported through repeated transactions.

## 14. Cumulative produced quantity?

Legacy accumulates `PrSchDailyProcess.TotalQty` and stores each individual transaction in `PrSchDailyProd`.

## 15. Overproduction prevention/tolerance?

Legacy protection is uneven. Prior-process WIP availability is now checked in later helper versions; robust unified output tolerance is not a clean legacy contract.

New ERP should implement an explicit output variance policy.

## 16. Work Centre/process completion states?

Legacy derives Work Order completion from final output totals, but process `Completed` logic contains disabled/inconsistent code. Do not copy it literally.

## 17. How does `FinalProcess` change behavior?

It identifies the Work Centre hand-off process. Its output can become eligible material for a later Work Centre.

## 18. When does WIP become Finished Goods inventory?

Through a separate FG posting/receipt flow (`FGS`), not automatically merely because Daily Production is on a final process.

## 19. UOM conversions?

Legacy has limited/implicit handling plus catch weight. New ERP already has explicit required/base UOM conversion and should own this rule.

## 20. Inventory costs?

Legacy inventory posting carries `UnitPrice`; FG helper calculates FG cost from raw material, labour, overhead logic. Cost behavior is spread across legacy helpers.

The new design should use the existing inventory costing/posting infrastructure and define WIP cost explicitly.

## 21. Edit/delete/reverse?

Posted Daily Production is not ordinarily editable. Rollback reverses Daily Production facts, WIP output/input balances, cumulative process and Work Order status in a transaction.

## 22. What prevents downstream-used WIP from being reversed?

Legacy rollback cannot subtract the full original output if the current output lot no longer has enough balance; it fails. New ERP should add explicit movement dependency checks.

## 23. How is concurrency handled?

Legacy uses transactions but has pre-lock race windows. New ERP Material Issue demonstrates the required SQL row-locking/idempotency approach and should be copied structurally.

## 24. How should future-stock rules apply?

Neither inventory nor WIP may be consumed if it did not exist as of the production transaction date. Use inventory as-of logic and implement equivalent WIP as-of logic.

## 25. What should not be copied?

Do not copy:

```text
Web Forms/session DataTables
raw SQL scattered across pages
duplicated helper variants
JONG/ANY customer exceptions as core behavior
PO_NO machine hack
Cost/UnitPrice used as unrelated production fields
double arithmetic
implicit UOM handling
mutable posted history
balance-only dependency detection
weak pre-lock validation
legacy process Completed flag behavior
```

---

# 36. Recommended Core Flow in the New ERP

```text
RELEASED WORK ORDER SNAPSHOT
ProductionWorkOrder
  │
  ├─ RouteStep
  │    └─ Operation
  │          ├─ Material
  │          ├─ Machine
  │          └─ Labour
  │
  ▼
DAILY PRODUCTION / OUTPUT
  │
  ├─ calculate processed qty
  ├─ calculate process material consumption
  │
  ├─ MANUAL material
  │      ↓
  │   consume prior ISSUE movements
  │
  ├─ BACKFLUSH material
  │      ↓
  │   inventory as-of allocation/posting
  │
  ├─ INTERNAL_ROUTE_WIP
  │      ↓
  │   consume producer-route WIP lot(s)
  │
  ├─ write immutable production output
  ├─ write immutable consumption movements
  │
  ├─ output type?
  │      ├─ WIP_STOCKED → produce WIP lot
  │      ├─ WIP_NONSTOCK → transferable quantity
  │      └─ FINISHED_GOODS → FG receipt workflow
  │
  ├─ rebuild operation/work-order projections
  └─ commit atomically
```

---

# 37. Evidence Index — Important Conclusions

## Finding A — Daily Production uses Work Order BOM

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/HelperEx/DailyPodOutPutHelper.cs

Method:
CalAllInputQty(...)

Call:
PrSchBOMBL.GetBOM(workOrderNo, RelNo, WCCode, ProcessCode)
```

Meaning:

```text
Daily Production follows the frozen Work Order process BOM.
```

---

## Finding B — Standard Daily Production consumes production/WIP material

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/Helper/ProdPlanHelper.cs

Method:
GetAllItemSql(...)

Table:
WIPItemBalLoc

Branches:
- STOCK material
- previous process WIP
- previous final-process Work Centre output
```

Meaning:

```text
Standard Daily Production consumes production-side material/WIP.
```

---

## Finding C — No-Issue mode consumes normal inventory

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/HelperEx/ProdPlanHelperEx.cs

Method:
GetAllItemSqlStock_ByWarehouse(...)

Table:
IvBalLoc
```

and:

```text
File:
ProductionPlan/Helper/DailyProdNoIssuePostHelper.cs

Call:
CPosting.PostInventoryTransaction("IP", ...)
```

Meaning:

```text
No-Issue Daily Production is direct warehouse backflush.
```

---

## Finding D — Future inventory stock is explicitly filtered in No-Issue mode

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/HelperEx/ProdPlanHelperEx.cs

Method:
GetAllItemSqlStock_ByWarehouse(...)

Condition:
b.TransDate <= transaction date
```

Meaning:

```text
The old code recognizes the as-of-stock rule, but not uniformly enough.
```

---

## Finding E — Daily Production creates WIP output

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/Helper/DailyProdPostHelper.cs

Methods:
PostToWIP(...)
PostWIPWithSchedule(...)

Table:
WIPItemBalLoc
```

Meaning:

```text
WI output becomes a production/WIP balance lot.
```

---

## Finding F — Daily Production writes detailed production history

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/Helper/DailyProdPostHelper.cs

Method:
PostWithSchedule(...)

Table:
PrSchDailyProd
```

Fields include:

```text
GoodQty
scrap/reject
lot
Work Order
WC/process
machine
operator
shift
source lot lineage
batch/ref
```

---

## Finding G — rollback is blocked when produced WIP has been consumed

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/Helper/DailyProdPostHelper.cs

Methods:
StartRollBack(...)
Rollback(...)
RollbackWIPWithSchedule(...)
```

The output lot quantity is reduced during rollback and a negative result produces an insufficient-quantity failure.

---

## Finding H — FG receipt is separate from Daily Production

**CONFIRMED FROM CODE**

Evidence:

```text
File:
ProductionPlan/HelperEx/DailyFGPostHelper.cs

TrxType:
FGS

Call:
CPosting.PostInventoryTransaction("FGS", ...)
```

Meaning:

```text
Final production output and normal inventory receipt are separate lifecycle stages.
```

---

## Finding I — new ERP already has correct material issue execution foundations

**CONFIRMED FROM CODE**

Evidence:

```text
ErpWeb.Core/Production/ProductionMaterialIssueService.*
ErpWeb.Core/Production/ProductionMaterialAllocationService.cs
ErpWeb.Core/Production/ProductionMaterialExecutionCalc.cs
ErpWeb.Model/Entities/Production/ProductionMaterialMovement.cs
ErpWeb.Model/Entities/Production/ProductionPostingLink.cs
```

Meaning:

```text
Daily Production should extend this architecture rather than recreate stock rules.
```

---

## Finding J — new Work Order snapshot does not retain route OutputType

**CONFIRMED FROM CODE**

Evidence:

```text
Product Definition:
ErpWeb.Model/Entities/Planning/ProductDefinitionPhase1Entities.cs
PrBomRouteStep.OutputType

Work Order:
ErpWeb.Model/Entities/Production/ProductionWorkOrderRouteStep.cs
(no OutputType)

Snapshot builder:
BuildRouteStep(...)
copies output item/base qty/UOM but not OutputType.
```

Meaning:

```text
This gap should be fixed before Daily Production execution is enabled.
```

---

# 38. Production-Readiness Decision

The legacy manufacturing concept is sound enough to carry forward:

```text
WO snapshot
→ process-specific material requirement
→ issued/backflush/WIP consumption
→ actual output
→ WIP lot
→ next operation / Work Centre
→ final output
→ FG receipt
```

But the old technical implementation should **not** be ported.

The new ERP already has most of the hard infrastructure that the legacy system lacked:

- explicit Work Order aggregate;
- operation-specific material ownership;
- immutable material movement facts;
- UOM conversions;
- tolerance logic;
- as-of inventory;
- row locking;
- idempotency;
- rollback dependency checks;
- posting links;
- audit;
- row versioning.

The missing production-execution layer should therefore be built as:

```text
Production Output document
+ immutable consumption facts
+ immutable WIP movement facts
+ WIP balance projection
+ FG receipt integration
+ projection rebuild
```

using the existing Material Issue and Inventory posting patterns.

---

# 39. Final Recommended Implementation Order

```text
1. Freeze RouteStep.OutputType into Work Order snapshot
2. Finalize yield/loss/output-tolerance semantics
3. Add ProductionOutput aggregate
4. Add immutable WIP movement ledger
5. Add WIP as-of/locking service
6. Add production material consumption resolver
7. Implement Output draft/read/list UI
8. Implement atomic Output posting
9. Implement Output rollback/dependency validation
10. Rebuild operation/WO progress projections from facts
11. Add FG receipt workflow
12. Add production inquiries / traceability
13. Only then open the production execution gate
```

---

# 40. Bottom Line

The correct port is **not**:

```text
DailyPrdBatchAll.aspx.cs
→ rewrite as Razor
```

The correct port is:

```text
extract legacy manufacturing rules
        ↓
map them onto the current Work Order snapshot
        ↓
reuse the already-strong Material Issue + Inventory posting infrastructure
        ↓
add a modern production-output/WIP execution layer
```

The most important architecture decision is:

> **Daily Production must be the transaction that records actual operation output and consumes the exact frozen Work Order material/WIP requirements for that output, while creating traceable downstream WIP/FG output — all atomically and reversibly.**

That preserves the proven behavior of ERPOrigin while avoiding its Web Forms, DataTable, raw-SQL, overloaded-field, weak-concurrency, and duplicated-helper technical debt.
