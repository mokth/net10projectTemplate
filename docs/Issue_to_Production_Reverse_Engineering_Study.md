# Deep Reverse-Engineering Study — Old ERP Issue to Production → New Blazor ERP

**Study date:** 2026-10-01  
**Old ERP repository:** `mokth/ERPOrigin` — `master` at `1282097754401af2b36ba7a0ab46cc83574e6253`  
**New ERP repository:** `mokth/net10projectTemplate` — `production` at `e1a41fed8114f3373bcabe99bfdcd8931b49bf40`  
**Purpose:** Reverse-engineer the real old ERP Issue-to-Production business behavior, then map the proven concepts into the existing new Blazor production and inventory architecture without copying the Web Forms design.

---

## Evidence labels used in this report

- **VERIFIED FROM OLD ERP** — directly supported by old ERP source code that was inspected.
- **VERIFIED FROM NEW ERP** — directly supported by the current `production` branch of the new ERP.
- **INFERRED FROM AVAILABLE CODE** — strongly implied by the code path but not fully proven end-to-end.
- **NOT VERIFIED** — the repository evidence inspected does not prove the point.
- **RECOMMENDED FOR NEW ERP** — design recommendation based on the verified behavior and the existing new architecture.

### Important scope limitation

The supplied prompt included a connection string for a local SQL Express database. This execution environment cannot directly connect to the user's local `mokth\SQLExpress` instance. Therefore, database behavior in this study was verified from repository SQL, table access code, posting routines, entities, and transaction logic rather than by querying the live local database. Any point that would require live-row inspection is explicitly marked **NOT VERIFIED** rather than guessed.

---

# A. Executive Summary

## A.1 What “Issue to Production” means in the old ERP

**VERIFIED FROM OLD ERP**

The old ERP implements Issue to Production as a real inventory movement, not merely a BOM reservation or a display-only production requirement.

The business meaning is:

```text
Warehouse / Location / Lot stock
            ↓
        IP posting
            ↓
Inventory stock decreases
            ↓
Production WIP balance increases
            ↓
Work Order / Work Center / Process production continues
            ↓
Daily production output consumes/moves WIP through later process stages
            ↓
Finished/WIP output is produced
```

This corresponds to the “warehouse → production/WIP” model rather than treating issue as immediate final consumption.

The key proof is the posting chain:

```text
IssueToProductionEx.aspx.cs
    ↓
ProdNeedPostHelper.PostIP(...)
    ↓
IssueProdPostHelper.Post(...)
    ↓
CPosting.PostInventoryTransaction("IP", ...)
    ↓
CPosting.PostIPTransaction(...)
    ↓
warehouse IvBalLoc / IvBalance reduced
IvTrxHistory written with cost
    ↓
CPosting.PostIPtoWIPBal(...)
    ↓
WIPItemBal / WIPItemBalLoc increased
    ↓
PrSchDailyProd IP record written
```

So the old ERP clearly separates these concepts:

```text
Planned BOM requirement ≠ material issued ≠ material consumed/output
```

That separation is important and should be preserved in the new ERP.

## A.2 The most important old ERP rule

**VERIFIED FROM OLD ERP**

Material requirements are based on the **Work Order BOM snapshot (`PrSchBOM`)**, not directly on the current Product Definition BOM (`PrDefBOM`). The Issue-to-Production loader filters by:

- Work Order (`ScheCode`)
- product
- Work Center
- production process
- default BOM line
- non-WIP-default material

This is correct ERP behavior because a released Work Order must preserve the material definition that applied to that order, even if the Product Definition changes later.

## A.3 Old material formula

**VERIFIED FROM OLD ERP**

The old helper derives a material-per-output ratio from the Work Order BOM and then calculates the issue requirement for the requested production quantity.

Conceptually:

```text
Material per output unit
    = Work Order BOM StdQty / scheduled output qty

Required material for requested output
    = Round(Material per output unit × Requested Output Qty, 4)
```

The current helper ultimately executes:

```csharp
newIssueQty = Math.Round(StdPackSize * reqQutput, 4);
```

and the caller derives `StdPackSize` from the Work Order BOM quantity/output basis.

Example:

```text
WO planned output = 1,000 PCS
WO BOM RM001      =   500 KG
Material ratio    = 0.5 KG / PCS

Requested output  = 200 PCS
Required issue    = 0.5 × 200 = 100 KG
```

A major limitation is that the calculation does **not** directly subtract historical issue quantity. Historical issue is loaded for warning/validation purposes. Therefore repeated applications using the same requested output basis can potentially create an over-issue scenario unless another validation/tolerance rule catches it.

## A.4 Old lot allocation

**VERIFIED FROM OLD ERP**

Available stock comes from `IvBalLoc` rows with active status and positive quantity. The helper orders available lots by `TransDate` ascending and allocates the required quantity across them. This is FIFO-like by stock transaction date.

It is **not proven to be FEFO**, even though expiry information is exposed in later code.

## A.5 Old stock and WIP posting

**VERIFIED FROM OLD ERP**

At IP posting:

1. inventory availability is rechecked;
2. the warehouse/location/lot balance is reduced;
3. inventory transaction history is written;
4. inventory cost is carried into history;
5. the issued material is added to WIP balances;
6. a `PrSchDailyProd` IP record is created;
7. the Work Order can move to `IN PROGRESS`.

Posting is reversible. Rollback restores stock, reverses WIP, handles inventory history/rollback history, and removes the IP production daily record.

## A.6 Key weaknesses in the old implementation

**VERIFIED / INFERRED FROM OLD ERP**

The business concepts are useful, but the implementation has several structural weaknesses:

- business rules are distributed across Web Forms code-behind, helpers, generic posting classes, and raw SQL;
- legacy field names are overloaded — e.g. batch `ProcessCode` is used for Work Center in parts of the IP flow, while the real production process is stored separately (`PreProcess`);
- historical issue aggregation is not consistently keyed to the exact Work Order BOM/operation occurrence;
- the old issue calculator does not inherently calculate `outstanding = required - net issued`;
- over-issue behavior is inconsistent between screen validation and tolerance logic;
- application/global posting locks are used instead of relying primarily on deterministic database row locks;
- UOM handling is much weaker and less explicit than the new ERP model;
- WIP and cost are real, but the execution facts are spread across generic inventory history, WIP balances, and production daily records.

## A.7 The new ERP is already structurally prepared

**VERIFIED FROM NEW ERP**

The current new ERP `production` branch already has much of the correct foundation:

- frozen Work Order snapshots;
- `ProductionWorkOrderMaterial` tied to the exact consuming `WorkOrderOperationId`;
- Product Definition BOM base quantity and scrap percentage;
- explicit required UOM and inventory base UOM;
- conversion factor to base;
- tolerance;
- alternate group;
- issue method (`MANUAL`, `BACKFLUSH`, `PICK_LIST`);
- supply source including `INTERNAL_ROUTE_WIP`;
- material warehouse/location defaults;
- execution projection fields: `ReservedQty`, `PickedQty`, `IssuedQty`, `ReturnedQty`, `ConsumedQty`, `VarianceQty`;
- robust inventory batch/history framework;
- inventory transaction constant `IP` already exists;
- SQL Server concurrency protections (`UPDLOCK`, `HOLDLOCK`, conditional stock decrement);
- posting and rollback audit fields;
- `ProductionPostingLink` idempotency infrastructure.

The model itself states that material execution projections remain zero until production material execution is implemented. This is the missing layer.

## A.8 Recommended final concept

**RECOMMENDED FOR NEW ERP**

Do not clone the old `IssueToProductionEx.aspx` design. Implement a new production material execution service around the existing Work Order material snapshot and existing inventory posting engine:

```text
Released / In-Progress Work Order
        ↓
ProductionWorkOrderMaterial
(exact consuming operation)
        ↓
Required / Net Issued / Outstanding / Available
        ↓
Material Issue document
        ↓
Warehouse / Location / Lot allocation
        ↓
ProductionMaterialIssueService
        ↓  same DB transaction
Existing inventory stock-out posting using TrxType = IP
        ↓
Immutable production material movement fact
        ↓
IssuedQty projection + WIP material position + actual cost
        ↓
Production output / consumption
        ↓
Return / reversal / variance
```

The new design should keep Issue and Consumption separate. That is one of the strongest concepts in the old ERP and is necessary for WIP, returns, shop-floor traceability, and actual costing.

---

# B. Old ERP Source-Code Map

## B.1 Primary execution chain

```text
ProductionPlan/ProdPlan/IssueToProductionEx.aspx
        ↓
ProductionPlan/ProdPlan/IssueToProductionEx.aspx.cs
        ↓
ProductionPlan/Helper/IssueProdHelper.cs
  class IssueToProductionHelper
        ↓
ProductionPlan/Helper/IssueToProdHelper.cs
        ↓
ERPClasses/BL/PrSchBOMBL.cs
        ↓
PrSchBOM / PrSchWCenter / PrSchMas / IvMas / IvBalLoc / IvTrxHistory
```

Posting chain:

```text
IssueToProductionEx.aspx.cs
        ↓ Save("yes")
ProductionPlan/HelperEx/NeedPostHelper.cs
  ProdNeedPostHelper.PostIP(...)
        ↓
ProductionPlan/Helper/IssueProdPostHelper.cs
  IssueProdPostHelper.Post(...)
        ↓
ERPClasses/Classes/CPosting.cs
  PostInventoryTransaction("IP", ...)
  PostIPTransaction(...)
  PostIPtoWIPBal(...)
        ↓
IvTrxBatch / IvTrxBatchDetail
IvBalLoc / IvBalance
IvTrxHistory
IvBalLocCost
WIPItemBal / WIPItemBalLoc
PrSchDailyProd
PrSchMas
```

Rollback chain:

```text
IssueProdPostHelper.Rollback(...)
        ↓
CPosting.RollbackInventoryTransaction("IP", ...)
        ↓
CPosting.RollbackIPTransaction(...)
CPosting.RollbackIPtoWIPBal(...)
        ↓
Stock restored
WIP reversed
IvTrxHistory removed/reversal history recorded
PrSchDailyProd IP record removed
WO status recalculated
```

## B.2 Important support files found

| Area | Old ERP source | Role |
|---|---|---|
| IP UI / staging | `ProductionPlan/ProdPlan/IssueToProductionEx.aspx.cs` | Main document creation, BOM loading, validation, save/post request |
| BOM requirement | `ProductionPlan/Helper/IssueProdHelper.cs` | Work Order BOM + lots + requirement calculation |
| BOM helper | `ProductionPlan/Helper/IssueToProdHelper.cs` | Load and validate schedule BOM, issue helper logic |
| WO BOM BL | `ERPClasses/BL/PrSchBOMBL.cs` | Work Order BOM queries and material pack ratio |
| IP posting orchestration | `ProductionPlan/HelperEx/NeedPostHelper.cs` | Posting access/status/global lock wrapper |
| IP production posting | `ProductionPlan/Helper/IssueProdPostHelper.cs` | Inventory + WIP + production daily + rollback coordination |
| Generic stock posting | `ERPClasses/Classes/CPosting.cs` | Stock availability, balance mutation, history, costing, WIP mutation |
| Issued list | `ProductionPlan/ProdPlan/LookUp/IssuedProdList.aspx.cs` | Reads production issue history from `PrSchDailyProd` |
| Output/WIP execution | `ProductionPlan/Helper/DailyProdJONGPostHelper.cs` and related DailyProd helpers | Moves WIP through production process and records output |
| Production validation | `ERPClasses/BL/ProductionBL.cs` | BOM/default/alternative validation logic |

## B.3 Important tables seen in the execution chain

| Table | Observed role |
|---|---|
| `PrSchMas` | Work Order / schedule header |
| `PrSchWCenter` | Work Order Work Center snapshot |
| `PrSchProcess` | Work Order process snapshot |
| `PrSchBOM` | **Authoritative material requirement snapshot for IP** |
| `IvTrxBatch` | Inventory transaction header/staging document |
| `IvTrxBatchDetail` | Inventory transaction issue lines |
| `IvBalLoc` | Warehouse/location/lot stock balance |
| `IvBalance` | Inventory balance summary |
| `IvTrxHistory` | Posted inventory movement history and cost |
| `IvTrxHistoryRollback` | Rollback/reversal history |
| `IvBalLocCost` | Inventory balance/location costing support |
| `WIPItemBal` | Production WIP summary balance |
| `WIPItemBalLoc` | Work Order/process/lot-level WIP balance |
| `PrSchDailyProd` | Posted production/IP/output execution history |
| `PrSchDailyProcess` | Production process progress |

---

# C. End-to-End Old ERP Workflow

## C.1 Verified lifecycle

```text
Product Definition
  PrDef*
        ↓
Work Order creation / snapshot
  PrSchMas
  PrSchWCenter
  PrSchProcess
  PrSchBOM
  PrSchMachine
  PrSchLabour
        ↓
Issue to Production selects Work Order + WC + Process
        ↓
Load PrSchBOM default material requirements
        ↓
Join active IvBalLoc stock by material / warehouse
        ↓
Allocate issue qty across lots (TransDate ascending)
        ↓
Create IvTrxBatch(IP/NEW) + IvTrxBatchDetail
        ↓
Post IP
        ↓
Check stock again
        ↓
Decrease IvBalLoc / IvBalance
        ↓
Write IvTrxHistory and costing data
        ↓
Increase WIPItemBal / WIPItemBalLoc
        ↓
Write PrSchDailyProd TrxType=IP
        ↓
Work Order may become IN PROGRESS
        ↓
Daily production / process output
        ↓
consume/move previous-process WIP
        ↓
create process/WIP/FG output
        ↓
complete / close Work Order
```

## C.2 Work Order eligibility

**VERIFIED FROM OLD ERP**

The main helper excludes Work Orders in `Completed` and `Closed` status from normal issue selection. The posting path also changes a released/active order toward `IN PROGRESS` after production execution begins.

**NOT VERIFIED**

A single centralized old status policy for every special page was not found during this study. There may be secondary/special production pages with different status behavior.

---

# D. Database Tables and Relationships

## D.1 Requirement relationship

**VERIFIED FROM OLD ERP**

The important relationship is:

```text
PrSchMas (Work Order)
   │ ScheCode
   ├── PrSchWCenter
   │      │ WCCode / WCICode
   │      └── PrSchProcess
   │             │ ProcessCode
   │             └── PrSchBOM
   │                    ICode / StdQty / Warehouse
   │
   └── execution
          IvTrxBatch / Detail (IP)
               ↓
          IvTrxHistory
               ↓
          WIPItemBalLoc
               ↓
          PrSchDailyProd
```

The issue transaction is not merely linked to a Work Order header. The requirement source is process-aware because the `PrSchBOM` query includes exact Work Center and Process.

## D.2 Legacy identifier issue

**VERIFIED FROM OLD ERP**

The staging/history fields are confusing:

- `ScheCode` = Work Order
- detail `ProcessCode` is used as Work Center in the IP path
- detail `WCProcessCode` is often set to `"STOCK"`
- detail `PreProcess` preserves the actual production process

This is a legacy implementation artifact and should **not** be copied.

---

# E. Work Order and BOM Relationship

## E.1 Does IP use `PrDefBOM` or `PrSchBOM`?

**VERIFIED FROM OLD ERP: `PrSchBOM`.**

`IssueToProductionHelper.GetSchBomAllLot(...)` selects BOM rows from `PrSchBOM` and filters the selected Work Order, product, Work Center, and process.

The current query includes:

```text
PrSchBOM b
JOIN PrSchWCenter w ... AND b.ScheCode = w.ScheCode
JOIN IvMas m ...
WHERE
    b.ScheCode = selected WO
    AND b.ProdCode = selected product
    AND b.WCCode = selected WC
    AND b.ProcessCode = selected process
    AND b.BomDefault = 1
    AND b.StdQty > 0
    AND WIPBomDefault = 0
```

A `PrDefBOM` join appears in legacy query versions for supplementary/default information, but it is **not the authoritative requirement source**.

## E.2 Why this is correct

The Work Order BOM is the released execution snapshot. If the Product Definition is changed later, an existing Work Order should not silently consume a new BOM definition.

**Decision for new ERP: KEEP.**

---

# F. Material Requirement Calculation

## F.1 Old ERP formula

**VERIFIED FROM OLD ERP**

The old helper uses a material-per-output ratio and requested production quantity:

```text
StdPackSize ≈ WorkOrderBomQty / WorkOrderScheduledOutputQty

IssueRequirement
= Round(StdPackSize × RequestedOutputQty, 4)
```

The key executable statement is:

```csharp
newIssueQty = Math.Round(StdPackSize * reqQutput, 4);
```

Older/commented logic expressed the same ratio more explicitly:

```text
(BOM Qty / Scheduled Output) × Requested Output
```

## F.2 Scrap/reject overload

Another helper overload calculates:

```text
Normal material = Round(StdPackSize × RequestedOutputQty, 4)
Scrap material  = Round(StdPackSize × ScrapQty, 4)
Reject material = Round(StdPackSize × RejectQty, 4)

Total allocation basis
= Normal + Scrap + Reject
```

## F.3 Historical issue is not deducted in the calculator

**VERIFIED FROM OLD ERP**

`IssuedQty` is loaded into the grouped calculation but is not subtracted from `newIssueQty` in the standard `CalculateReqBOM` method.

Therefore this is **not** the old formula:

```text
Issue Now = Requirement - Already Issued
```

Instead the screen calculates the requirement for the operator's requested output basis and uses issued quantity elsewhere for comparison/warning.

This is a material weakness for repeated issue scenarios.

## F.4 New ERP formula is better

**VERIFIED FROM NEW ERP**

`WorkOrderQuantityCalculator.CalculateMaterialAsync(...)` implements:

```text
OperationMaterialBasisQty
= consuming operation output converted into BOM denominator UOM

RequiredQty
= Round4(
    OperationMaterialBasisQty
    × ComponentQtyPerParent
    ÷ BomOutputQty
    × (1 + ScrapPercent / 100)
  )
```

Then it converts `RequiredQty` into inventory base UOM and stores:

```text
RequiredBaseQty
ConversionFactorToBase
```

Tolerance is explicitly **not** added into `RequiredQty`; it is treated as an execution/variance control.

**Recommendation: use the new formula and snapshot, not the old pack-size calculation implementation.**

---

# G. Required / Issued / Returned / Outstanding Logic

## G.1 Old ERP

### Required Qty

**VERIFIED** — derived from the Work Order BOM ratio and requested output as described above.

### Issued Qty

**VERIFIED** — `IssueToProductionHelper` reads posted `IvTrxHistory` where `TrxType='IP'` and aggregates quantity.

A legacy weakness is that this aggregation is primarily by:

```text
ScheCode + ProdCode + ProcessCode + ICode
```

and in the legacy IP mapping the history `ProcessCode` corresponds to Work Center. Therefore it does not provide a clean immutable key to the exact consuming Work Order BOM/operation occurrence.

### Returned Qty

**PARTIALLY VERIFIED** — production helper logic recognizes `PrSchDailyProd.TrxType='SR'` as a return/negative issue movement. Various production/subcontract paths use `SR`. However a single complete factory material-return page/posting chain equivalent to the IP chain was not fully traced in this study.

### Outstanding Qty

**NOT VERIFIED AS A SINGLE AUTHORITATIVE OLD FORMULA.**

The IP UI compares required quantity, current issue quantity, stock, and historical issued quantity, but the old calculator does not directly maintain a clean persistent `OutstandingQty` field.

## G.2 Recommended new ERP formulas

**RECOMMENDED FOR NEW ERP**

Use immutable posted movement facts and rebuild the Work Order material projections from them:

```text
NetIssuedQty
= PostedIssueQty - PostedReturnQty

OutstandingQty
= max(0, RequiredQty - NetIssuedQty)

OverIssueQty
= max(0, NetIssuedQty - RequiredQty)

WipMaterialQty
= PostedIssueQty - PostedReturnQty - PostedConsumedQty ± Adjustments

VarianceQty
= ConsumedQty - RequiredQtyForActualOutput
```

Do not make `IssuedQty` synonymous with `ConsumedQty`.

---

# H. Warehouse / Location / Lot / UOM Logic

## H.1 Old warehouse and location selection

**VERIFIED FROM OLD ERP**

`PrSchBOM.Warehouse` is used when joining candidate `IvBalLoc` rows. The UI/staging detail records the selected source warehouse, source location, and source lot.

The old source priority includes the BOM's warehouse and then the specific available stock slice selected from `IvBalLoc`.

A universal old fallback priority across every scenario (BOM warehouse → item default → user default, etc.) was **NOT VERIFIED**.

## H.2 Stock availability

**VERIFIED FROM OLD ERP**

The BOM lot loader uses active positive `IvBalLoc` stock. Posting rechecks the exact stock slice.

`CPosting.CheckQuantityAvailable` compares required issue quantities against the selected `IvBalLoc` quantity and rejects insufficient stock for stock-controlled items.

Therefore normal IP posting does **not** intentionally permit a warehouse lot to go negative.

## H.3 Multiple lots

**VERIFIED FROM OLD ERP**

One material requirement can be allocated across multiple `IvBalLoc` rows/lots. The requirement calculator walks the candidate stock rows until the requirement is filled.

## H.4 FIFO/FEFO

**VERIFIED** — candidate rows are ordered by `IvBalLoc.TransDate` ascending.

**INFERRED** — this is a FIFO-like allocation policy.

**NOT VERIFIED** — FEFO is not proven. Expiry data is exposed but the inspected allocation routine does not sort by expiry.

## H.5 UOM

The old ERP stores standard UOM and catch-weight quantities, and availability checking includes standard and catch-weight quantities.

However, the old IP flow does not have the explicit, strongly modeled UOM chain present in the new ERP.

**RECOMMENDED** — new material issue must use the existing new ERP UOM conversion service and post inventory in `RequiredBaseQty/BaseUom`. Do not create a second production-only UOM conversion implementation.

---

# I. Inventory Posting Logic

## I.1 Old posting effects

**VERIFIED FROM OLD ERP**

At post:

```text
IvTrxBatch (IP)        NEW → POSTED
IvTrxBatchDetail       source movement lines
IvBalLoc               quantity decreases
IvBalance              inventory summary adjusts
IvTrxHistory           posted movement + cost written
IvBalLocCost           costing balance adjusted
WIPItemBal             WIP increases
WIPItemBalLoc          WO/process/lot WIP increases
PrSchDailyProd         IP production execution record created
PrSchMas               can become IN PROGRESS
```

`IssueProdPostHelper.UpdateRec(SqlTransaction)` persists these changes inside a SQL transaction.

## I.2 Inventory direction

**VERIFIED FROM OLD ERP**

```text
Raw material inventory OUT
        ↓
Production WIP IN
```

It is not merely a financial memo and not immediate final material consumption.

## I.3 Cost

**VERIFIED**

The selected stock row's unit price is carried into the IP detail/posting path. Inventory history and WIP receive the posted value/cost information.

**NOT FULLY VERIFIED**

The entire upstream valuation algorithm that originally established `IvBalLoc.UnitPrice` was not fully reverse engineered here. Therefore this report does not claim that the old ERP's accounting valuation is strictly FIFO, moving-average, or another method solely from the Issue-to-Production code.

The **selection** behavior is FIFO-like; that is not automatically the same thing as the valuation method.

---

# J. WIP Behaviour

## J.1 Old ERP WIP model

**VERIFIED FROM OLD ERP**

The old ERP has physical/logical production WIP balances:

```text
WIPItemBal
WIPItemBalLoc
```

`PostIPtoWIPBal` increases WIP when raw material is issued.

Later daily production helpers update `WIPItemBalLoc`, including logic explicitly described in code as deleting quantity from the previous process. This proves that WIP is carried through production/process execution rather than issue being treated as immediate final consumption.

## J.2 New ERP implication

**RECOMMENDED FOR NEW ERP**

Keep the conceptual WIP distinction, but do not copy the old WIP tables blindly.

The current new `ProductionWorkOrderMaterial` already has:

```text
IssuedQty
ReturnedQty
ConsumedQty
VarianceQty
```

What is still missing is the immutable line-level production material execution fact that can explain those projections by:

- issue document;
- Work Order material line;
- exact operation;
- warehouse/location/lot;
- quantity/UOM/base quantity;
- actual posted cost;
- return/consume/reversal lineage.

A production material movement ledger/fact should be the source; aggregate fields on `ProductionWorkOrderMaterial` should remain rebuildable projections.

---

# K. Costing Logic

## K.1 Old ERP material cost

**VERIFIED FROM OLD ERP**

The IP issue line captures source unit price from inventory location/lot. The generic inventory posting layer writes cost information to `IvTrxHistory` and updates location costing. WIP posting carries unit price/cost into WIP.

Thus actual material issue cost is based on the actual stock slice selected, not simply the BOM standard quantity alone.

## K.2 Work Order total cost

**INFERRED / PARTIALLY VERIFIED**

The old production subsystem also has labour, machine, output, WIP, and production-cost related structures, but a single end-to-end equation proving:

```text
Material + Labour + Machine + Overhead = final production cost
```

for every production mode was not fully traced in this Issue-to-Production study.

## K.3 Recommendation

**RECOMMENDED FOR NEW ERP**

When an IP stock-out posts, capture actual cost from the resulting authoritative inventory history into the production material movement fact. Never recalculate historical WIP material cost later from current item cost.

This allows:

```text
Actual Material Cost
= Σ posted material movement base qty × captured actual unit cost
```

Then future FG costing can combine immutable actual material cost with machine/labour/overhead execution costs.

---

# L. Return / Cancellation / Reversal Logic

## L.1 IP rollback

**VERIFIED FROM OLD ERP**

Posted IP can be rolled back through a real reversal flow. It is not simply deleted.

The rollback path:

- validates period/status;
- calls inventory rollback;
- restores stock quantities/costing;
- verifies/reverses WIP;
- removes/reverses production daily IP facts;
- writes rollback history/audit data;
- recalculates Work Order state when appropriate.

This is an important concept to keep.

## L.2 Material return

**PARTIALLY VERIFIED FROM OLD ERP**

`SR` is recognized in production execution as a return and is represented as negative issue in helper reporting. However the complete non-subcontract operator workflow for unused-material return was not traced to the same level as IP posting.

## L.3 Recommended new behavior

**RECOMMENDED FOR NEW ERP**

Separate these operations:

```text
1. Rollback/Reversal
   Corrects an erroneous posted IP, subject to downstream dependency checks.

2. Return From Production
   A legitimate later business movement of unused material from WIP back to inventory.
```

A posted issue should never be freely edited or physically deleted.

---

# M. Production Output Relationship

## M.1 Old ERP

**VERIFIED FROM OLD ERP**

Production output is process-aware and interacts with WIP. `DailyProdJONGPostHelper` and related helpers:

- validate Work Order/Work Center/process BOM;
- update `PrSchDailyProcess`;
- add `PrSchDailyProd` output records;
- update Work Order status/progress;
- update WIP balances;
- deduct previous-process WIP when material/output moves forward;
- record actual good/scrap/reject quantities and unit price information.

Therefore IP provides material to production; later output transactions move/consume that WIP and create subsequent WIP/finished output.

## M.2 Critical design principle

**RECOMMENDED FOR NEW ERP**

Do not let Production Output simply overwrite `IssuedQty` or assume that every issued material has already been consumed.

A future output/backflush command should post explicit `CONSUME` material movement facts, after which:

```text
ConsumedQty
```

is updated from those facts.

---

# N. Old ERP Weaknesses — KEEP / IMPROVE / REPLACE / REMOVE

| Old concept / implementation | Decision | Reason |
|---|---|---|
| Work Order BOM snapshot drives execution | **KEEP** | Correctly freezes production requirement |
| Work Center + Process-level material ownership | **KEEP** | Needed for shop-floor and WIP traceability |
| Partial issue / multiple IP documents | **KEEP** | Normal production requirement |
| Multiple lot issue | **KEEP** | Required for lot traceability |
| Warehouse stock → WIP model | **KEEP** | Correct distinction between issue and consumption |
| Posted history and true rollback | **KEEP** | Audit/cost integrity |
| FIFO-like default allocation | **IMPROVE** | Retain as option; support FEFO for expiring materials |
| BOM requirement formula | **IMPROVE** | New ERP already has stronger base-qty + scrap + UOM formula |
| Historical issued calculation | **REPLACE** | Legacy grouping is not tied to exact material/operation identity |
| “Issue requirement” without automatic outstanding subtraction | **REPLACE** | New UI should explicitly calculate net issued/outstanding |
| Over-issue/tolerance behavior | **REPLACE** | Make one clear policy based on material tolerance |
| Alternative BOM semantics via `BomDefault` | **IMPROVE** | New ERP has explicit alternate groups and provenance |
| Raw SQL/string filtering in UI/helper classes | **REPLACE** | Use typed EF/repositories/services |
| Core business logic in `.aspx.cs` | **REPLACE** | Put execution rules in domain/application services |
| Global posting lock | **REPLACE** | Use deterministic SQL row locks + idempotency + transaction |
| Legacy `ProcessCode/WCProcessCode/PreProcess` overloading | **REMOVE** | Use real WorkOrderOperationId / route-step IDs |
| Live Product Definition join as execution authority | **REMOVE** | Work Order snapshot must remain authoritative |
| Free edit/delete of posted movement | **REMOVE** | Explicit return/reversal only |
| `CostPrice=0` staging hacks | **REMOVE** | Capture cost from authoritative inventory posting result |

---

# O. Old ERP → New ERP Mapping

| Business concept | Old ERP | New ERP existing component | Recommendation |
|---|---|---|---|
| Product Definition | `PrDef*` | `PrBomHdr`, `PrDefBOM`, operations/route steps | Reuse new model |
| Work Order | `PrSchMas` | `ProductionWorkOrder` | Reuse |
| WO Work Center | `PrSchWCenter` | `ProductionWorkOrderRouteStep` / operation ownership | Reuse modern graph |
| WO Process | `PrSchProcess` | `ProductionWorkOrderOperation` | Reuse |
| WO BOM | `PrSchBOM` | `ProductionWorkOrderMaterial` | **Primary new execution source** |
| Legacy compatibility BOM | `PrSchBOM` | `PrSchBom` entity exists | Keep only for migrated/compatibility paths |
| Material requirement | `StdQty` ratio | `RequiredQty`, `RequiredBaseQty` | Reuse new calculator |
| BOM alternate | `BomDefault=0` informal alternative | `AlternateGroupCode` | Reuse new explicit model |
| Issue method | implicit/manual | `IssueMethod` | Reuse; future backflush/pick-list ready |
| Supply source | implicit | `SupplySource` | Reuse; critical for internal-route WIP |
| Material issue document | `IvTrxBatch`/`Detail` IP | `IvTrxBatch`/`Detail`, IP constant exists | Reuse inventory batch for stock side; add minimal production execution document/fact link |
| Inventory posting | `CPosting` | `IIvInventoryPostingService` / `IvInventoryPostingService` | Reuse new engine |
| Inventory history | `IvTrxHistory` | `IvTrxHistory` | Reuse |
| Lot stock | `IvBalLoc` | `IvBalLoc` + repository locks | Reuse |
| WIP balance | `WIPItemBal/Loc` | No equivalent complete material-execution ledger found | Add production material movement source-of-truth; projection can represent WIP |
| Issue/return/consume projection | spread over tables | fields already on `ProductionWorkOrderMaterial` | Rebuild from posted facts |
| Idempotency link | application/global locks | `PrProductionPostingLink` | **Reuse** |
| Concurrency | global posting lock + SQL tx | DB row locks + conditional updates | Reuse new mechanism |
| Audit | mixed updated/history fields | explicit post/rollback/audit fields | Reuse and extend production document audit |

---

# P. Recommended New ERP Business Design

## P.1 Source of truth

**RECOMMENDED FOR NEW ERP**

For modern Work Orders:

```text
ProductionWorkOrderMaterial
```

must be the requirement source.

Do not query live `PrDefBOM` when issuing material to a released Work Order.

For legacy imported Work Orders, an adapter may read `PrSchBom`, but it should normalize into the same execution service contract.

## P.2 Work Order status policy

Recommended issue eligibility:

```text
RELEASED    → allowed
IN_PROGRESS → allowed
DRAFT       → blocked
COMPLETED   → blocked for normal issue
CANCELLED   → blocked
```

A controlled correction/change-order path can handle exceptional historical adjustment; normal material issue must not mutate completed/cancelled production casually.

## P.3 Quantities shown to operator

For each Work Order material/operation:

```text
Required
Issued
Returned
Net Issued
Consumed
Outstanding
WIP Balance
Available Stock
Shortage
Issue Now
```

Recommended definitions:

```text
Net Issued = Issued - Returned
Outstanding = max(0, Required - Net Issued)
WIP Balance = Net Issued - Consumed ± WIP adjustments
Shortage = max(0, Outstanding - AvailableToIssue)
```

## P.4 Partial and repeated issue

Allow both.

Example:

```text
Required = 100 KG
Issue 1 = 40 KG
Issue 2 = 30 KG
Net Issued = 70 KG
Outstanding = 30 KG
```

The next issue screen should default `Issue Now` to 30 KG, not recalculate another 100 KG from the same production quantity.

## P.5 Over-issue

Use the material tolerance explicitly:

```text
MaxAllowedNetIssue
= RequiredQty × (1 + TolerancePercent / 100)
```

Recommended default behavior:

- if `Tolerance = 0`, net issue cannot exceed required;
- if tolerance > 0, issue may exceed required only up to the tolerated maximum;
- never use current stock availability as the only over-issue control;
- if future business requires exceptional override, introduce a specific permission and reason/audit entry rather than silently allowing it.

## P.6 Substitutes / alternates

The new ERP already supports `AlternateGroupCode` and draft material substitution.

Recommended execution rule:

- before release: substitute through the existing Work Order snapshot workflow;
- after release: substitution should require a controlled Work Order change order or an explicit material deviation command;
- posted actual movement must record the **actual issued item** and its relationship to the original Work Order material requirement;
- cost comes from the actual substitute's inventory layer/slice.

## P.7 Supply-source behavior

Use `ProductionWorkOrderMaterial.SupplySource`:

```text
PURCHASED / EXTERNAL stock source
    → inventory warehouse issue

INTERNAL_ROUTE_WIP
    → consume from producing route-step WIP, not normal warehouse raw stock

SEPARATE_PRODUCT_DEFINITION
    → follow separately produced component/work-order supply semantics
```

This is important for future multi-stage production and should be respected from the first material execution design.

---

# Q. Recommended Database Changes

## Q.1 Reuse first — do not duplicate these

**VERIFIED FROM NEW ERP — ALREADY EXISTS**

Do not create replacements for:

```text
ProductionWorkOrder
ProductionWorkOrderOperation
ProductionWorkOrderMaterial
IvTrxBatch
IvTrxBatchDetail
IvTrxHistory
IvBalLoc
PrProductionPostingLink
```

Also reuse existing UOM conversion and inventory posting infrastructure.

## Q.2 What is genuinely missing

The current generic inventory detail/history does not carry a clean production line identity such as:

```text
WorkOrderMaterialId
WorkOrderOperationId
production movement semantic (ISSUE / RETURN / CONSUME / ADJUST)
original movement/reversal lineage
actual production WIP cost fact
```

`ProductionWorkOrderMaterial` contains aggregate projection fields, but those fields alone cannot provide immutable lot/cost/document-level history.

### Recommended minimal addition

Create a production material execution fact/ledger (name can follow project naming standards, e.g. `PrMaterialMovement`) with an immutable row per posted material movement.

Minimum conceptual fields:

```text
UID
CompanyCode
BranchCode
WorkOrderId
WorkOrderMaterialId
WorkOrderOperationId
MovementType          ISSUE / RETURN / CONSUME / ADJUST / REVERSAL
InventoryBatchNo
InventoryHistoryId or inventory line identity
ItemCode
WarehouseCode
LocationCode
LotId / LotNo
Qty
UOM
BaseQty
BaseUOM
UnitCost
TotalCost
PostingRequestId / PostingLinkId
OriginalMovementId   nullable, for reversal/return lineage
MovementDate
PostedBy
CreatedDate
```

This ledger should be the source for rebuilding:

```text
IssuedQty
ReturnedQty
ConsumedQty
VarianceQty
WIP material balance
```

### Why a new production movement fact is justified

Without it, the new system would repeat an old weakness: inventory history would know stock moved, but production would not have a stable typed link from that stock movement to the exact Work Order material/operation requirement.

### Do we need a separate WIP balance table immediately?

**Recommendation: no, not initially.**

Start with immutable movement facts and `ProductionWorkOrderMaterial` projections. Derive WIP position from movement facts. If later performance requires it, add a rebuildable WIP balance projection — not a second independent source of truth.

## Q.3 Material issue header/lines

Two viable designs exist:

### Preferred production-domain design

Use a small production material issue aggregate (`PrMaterialIssue` + line) for operator workflow/status and translate it into `IvTrxBatch`/`IvTrxBatchDetail` for stock posting. This gives strong production semantics and clean line-to-material identity.

### Leaner alternative

Use `IvTrxBatch`/`IvTrxBatchDetail` as the issue document itself and add a production link table that maps each inventory detail line to `WorkOrderMaterialId`/`WorkOrderOperationId`.

For this project, the preferred design is the first approach **only if** the UI needs its own production-document lifecycle (draft, line allocation, post, return references). If issue is always saved-and-posted as a simple command, the lean link-table approach is sufficient.

Do not add both unnecessarily.

## Q.4 Reuse `PrProductionPostingLink`

**VERIFIED FROM NEW ERP**

`ProductionPostingLink` already exists specifically for later material/WIP/FG commands and already has:

- `PostingRequestId`;
- `WorkOrderId`;
- production document type/no/line;
- inventory batch number;
- posting operation id;
- original posting link;
- status/result;
- a unique idempotency key.

Therefore do **not** create another general idempotency table.

---

# R. Recommended Blazor UI

## R.1 Main Issue to Production page

Suggested layout:

```text
Issue to Production
────────────────────────────────────────────────────────────
Work Order : WO000123       Status : RELEASED
Product    : FG001          Planned Qty : 1,000 PCS
Operation  : WC1 / Process 10
Issue Date : 2026-10-01

Material   Required  Net Issued  Outstanding  Available  Issue Now
RM001       500.00     300.00       200.00      450.00     200.00
RM002       100.00      80.00        20.00       10.00      10.00
RM003        50.00       0.00        50.00      100.00      50.00

[Auto Fill Outstanding] [Show Shortages] [Select Lots] [Post Issue]
```

## R.2 Process-aware navigation

Because the new model has real operation identity, let the user select:

```text
Work Order
  → Route / Work Center
      → Operation / Process
          → Materials
```

Also support an “All outstanding materials” mode for store personnel, but still preserve each issue line's `WorkOrderOperationId`.

## R.3 Lot selection popup

Show:

```text
Lot
Warehouse
Location
On Hand
Allocated/Available if applicable
Expiry
Receipt/Stock Date
Suggested Qty
Issue Qty
```

Default auto allocation should be configurable:

- FEFO for expiry-controlled material;
- FIFO/oldest receipt for non-expiring material;
- manual override with validation.

## R.4 Do not overload screen

The everyday operator should mainly see:

```text
Material
Required
Outstanding
Available
Issue Now
Warehouse/Location
Lot status
```

Detailed UOM conversion, source BOM version, costs, movement IDs, and audit trail belong in drill-down panels.

---

# S. Recommended Service Architecture

```text
PrMaterialIssueEntry.razor
        ↓
PrMaterialIssueEntry.razor.cs
  UI state only
        ↓
IProductionMaterialIssueService
        ↓
ProductionMaterialIssueService
  - eligibility
  - requirement/outstanding
  - tolerance
  - issue/return/reversal orchestration
  - idempotency
  - production movement facts
        ↓
IWorkOrderMaterialExecutionService
  - lock/read Work Order + material + operation
  - rebuild material projections
  - WIP position
        ↓
IIvInventoryPostingService
  existing stock engine
        ↓
IvStockPostingRepository
  existing UPDLOCK/HOLDLOCK + conditional update
        ↓
EF Core / SQL Server
```

Optional services:

```text
IMaterialAllocationService
  FIFO / FEFO / manual lot allocation

IProductionMaterialCostService
  maps authoritative inventory posting cost into production movement cost

IProductionMaterialInquiryService
  shortage / movement / actual-vs-standard inquiry
```

Critical rule: `.razor.cs` must not contain stock posting, tolerance, outstanding, WIP, or costing rules.

---

# T. Transaction / Concurrency Strategy

## T.1 Old ERP behavior

**VERIFIED FROM OLD ERP**

The old ERP uses SQL transactions but also uses an application/global posting lock mechanism around IP posting.

This reduces concurrent posting risk but is broad and does not provide the same precision as deterministic database row locking.

## T.2 New ERP inventory engine is stronger

**VERIFIED FROM NEW ERP**

The new stock posting code already:

- locks inventory batch rows;
- locks stock masters;
- locks `IvBalLoc` using `UPDLOCK, HOLDLOCK`;
- locks stock slices in deterministic order;
- aggregates required quantity per source balance row;
- rechecks on-hand quantity while locked;
- performs conditional SQL decrement with `AND StdQty >= @qty`;
- rejects posting if the update affects zero rows;
- rejects duplicate history for a batch;
- writes posting operation IDs and audit fields;
- supports rollback.

This solves the classic race:

```text
Stock = 100
User A wants 80
User B wants 50
```

Only one transaction can successfully consume stock such that the protected balance remains sufficient.

## T.3 Recommended IP transaction boundary

A production material issue should use **one database transaction** covering both inventory and production facts:

```text
BEGIN TRANSACTION

1. Resolve PostingRequestId / lock or insert ProductionPostingLink
2. Lock Work Order and affected WorkOrderMaterial rows
3. Verify WO status and snapshot revision
4. Recalculate authoritative Required / NetIssued / MaxAllowed
5. Validate issue qty and tolerance
6. Build/validate inventory IP batch lines
7. Call PostStockOutInTransactionAsync(..., expectedTrxType: "IP")
8. Read/capture authoritative posted cost/history identity
9. Insert production material movement facts
10. Rebuild Issued/Returned/Consumed/Variance projections
11. Transition RELEASED → IN_PROGRESS if appropriate
12. Complete ProductionPostingLink
13. SaveChanges

COMMIT
```

Any failure must roll back the whole unit.

## T.4 Idempotency

Use existing `PrProductionPostingLink`:

```text
Company + Branch + CommandType + PostingRequestId
```

If the client retries the same post after a timeout, the same command must not create a second stock issue.

---

# U. Future Production Compatibility

The recommended design is compatible with:

### MRP

MRP reads `RequiredBaseQty`, supply source, warehouse, net issued, and availability to calculate shortage.

### Production planning

Operation-owned materials allow shortage by Work Center/process/time bucket.

### Finite capacity / machine scheduling

Material readiness can become a constraint against the operation's planned start time.

### Pick list / staging

`IssueMethod=PICK_LIST` is already modeled. Add reserve/pick movement stages without changing the Work Order BOM schema.

### Backflush

`IssueMethod=BACKFLUSH` can automatically create issue/consume movements from posted output while preserving the same movement ledger and inventory transaction engine.

### Shop-floor mobile/barcode

Barcode scanning can resolve Work Order → operation → material → lot, then call the same service. Do not implement separate mobile posting logic.

### WIP

Internal route WIP is already modeled by `SupplySource=INTERNAL_ROUTE_WIP` and producer route step identity. The movement ledger can represent route-to-route WIP transfer/consume without abusing warehouse stock.

### Lot traceability

Because each movement records actual stock lot and Work Order material/operation identity, backward and forward traceability becomes possible:

```text
supplier/receipt lot
    → material issue
    → Work Order operation
    → WIP/output lot
    → finished goods lot
```

### Quality, scrap, rework

Use explicit movement types/reasons and production execution documents instead of adjusting aggregate fields directly.

### Actual costing / variance

Immutable issue cost + actual consume/output quantities allow:

```text
standard requirement vs actual consumption
standard material cost vs actual material cost
scrap/reject variance
substitute material variance
```

---

# V. Concrete Implementation Plan for AI Coding Agent

## Phase 1 — Freeze the execution contract

1. Treat `ProductionWorkOrderMaterial` as the modern requirement source.
2. Define issue-eligible Work Order statuses.
3. Define exact quantity formulas:
   - required;
   - net issued;
   - outstanding;
   - maximum allowed using tolerance;
   - WIP balance.
4. Define production movement types and return/reversal semantics.
5. Decide whether the UI needs a production issue header/line aggregate or only a production line-link around `IvTrxBatch`.

**Exit condition:** no stock code yet; domain contract reviewed.

## Phase 2 — Production material read/inquiry service

Implement a read service that returns by Work Order/operation:

```text
RequiredQty / UOM
RequiredBaseQty / BaseUOM
Issued
Returned
NetIssued
Consumed
Outstanding
Tolerance / MaxAllowed
Warehouse / Location defaults
Available stock
Shortage
lot-control status
alternate group / actual selected material
```

Do not use live Product Definition values for a released order.

## Phase 3 — Immutable production movement persistence

Add the minimum production material execution fact/link schema decided in Phase 1.

Requirements:

- exact `WorkOrderMaterialId`;
- exact `WorkOrderOperationId`;
- inventory batch/history identity;
- lot/source stock slice;
- base qty/UOM;
- actual cost;
- movement type;
- reversal lineage;
- audit fields.

Add indexes for:

```text
WorkOrderId + WorkOrderMaterialId + MovementType
InventoryBatchNo
OriginalMovementId
WorkOrderOperationId
```

## Phase 4 — Material allocation service

Implement:

- exact warehouse/location filter;
- lot-controlled vs non-lot material;
- FIFO default for non-expiry material;
- FEFO for expiry-controlled material;
- manual lot override;
- future-stock exclusion where applicable;
- base-UOM quantity normalization.

The allocator proposes lines; posting remains authoritative and revalidates stock.

## Phase 5 — IP posting command

Implement `ProductionMaterialIssueService.PostAsync(...)`.

Inside one EF/SQL transaction:

1. enforce `PrProductionPostingLink` idempotency;
2. lock Work Order/material rows;
3. validate Released/InProgress;
4. recompute net issued/outstanding/tolerance from posted facts;
5. create or validate inventory `IP` batch/detail rows;
6. invoke existing `PostStockOutInTransactionAsync(..., "IP")`;
7. capture inventory cost/history;
8. create production movement facts;
9. rebuild `ProductionWorkOrderMaterial.IssuedQty` and other projections;
10. update Work Order to InProgress when first execution occurs;
11. commit link and transaction.

Do **not** add IP to the generic public dispatcher unless there is a clear use case; production posting should go through the production service so a user cannot create stock IP without Work Order/WIP facts.

## Phase 6 — Blazor Issue-to-Production UI

Create:

```text
PrMaterialIssueEntry.razor
PrMaterialIssueEntry.razor.cs
PrMaterialIssueEntry.razor.css
```

Features:

- Work Order selector limited to issue-eligible orders;
- route/operation focus;
- material requirement grid;
- outstanding auto-fill;
- shortage display;
- lot allocation popup;
- post confirmation;
- no business posting logic in UI.

## Phase 7 — Return From Production

Implement explicit return document/command:

- reference Work Order material and preferably original issue movement;
- cannot return more than WIP material available;
- stock-in occurs in the same transaction as production return fact;
- original cost should be preserved/reconciled, not replaced with today's item cost;
- update `ReturnedQty` and WIP position.

Standardize the inventory transaction type for production return after checking project-wide naming conventions; do not overload customer return `CR`.

## Phase 8 — IP reversal

Implement rollback/reversal using the existing inventory rollback engine only when downstream production dependencies permit it.

Block rollback if, for example, material has already been consumed or later production output depends on the issued movement, unless an explicit compensating correction workflow is used.

Never physically delete posted history.

## Phase 9 — Production consumption/output integration

When Daily Production / Output is implemented:

- issue remains separate;
- consume WIP with explicit `CONSUME` facts;
- backflush can create issue + consume automatically where configured;
- update `ConsumedQty` from facts;
- preserve lot genealogy;
- calculate actual material variance.

## Phase 10 — Inquiries

Add:

```text
Material Issue History by WO
WO Material Requirement vs Issued vs Consumed
Material Shortage by WO / operation / date
WIP Material by WO / operation / lot
Actual vs Standard Material Usage
Material Cost Variance
Lot Traceability
```

## Phase 11 — Production-grade concurrency tests

At minimum test SQL Server concurrency for:

```text
same stock slice, two simultaneous IP posts
same WO material, two simultaneous issue posts
idempotent client retry
post vs rollback race
issue vs Work Order cancellation
lot allocation race
return vs consume race
```

The existing SQL Server inventory concurrency test approach can be reused.

---

# Required Questions — Direct Answers

| # | Question | Finding |
|---:|---|---|
| 1 | What exactly does Issue to Production mean in old ERP? | **VERIFIED:** Warehouse/location/lot stock is posted out and material is posted into production WIP, with production execution history. It is not final consumption. |
| 2 | Which pages implement it? | **VERIFIED:** Primary page is `ProductionPlan/ProdPlan/IssueToProductionEx.aspx(.cs)` plus lookup/history pages including `IssuedProdList`; other production pages interact with its WIP/output results. |
| 3 | Which classes contain real logic? | **VERIFIED:** `IssueToProductionHelper`, `IssueToProdHelper`, `PrSchBOMBL`, `ProdNeedPostHelper`, `IssueProdPostHelper`, `CPosting`, plus DailyProd output helpers. |
| 4 | Which DB tables are involved? | **VERIFIED:** `PrSchMas`, `PrSchWCenter`, `PrSchProcess`, `PrSchBOM`, `IvTrxBatch`, `IvTrxBatchDetail`, `IvBalLoc`, `IvBalance`, `IvTrxHistory`, costing tables, `WIPItemBal`, `WIPItemBalLoc`, `PrSchDailyProd`, etc. |
| 5 | How is it linked to WO? | **VERIFIED:** `ScheCode` + Work Center + process requirement; production history/WIP also carries WO references. |
| 6 | Product Definition BOM or WO BOM? | **VERIFIED:** Work Order BOM `PrSchBOM`. |
| 7 | Required material formula? | **VERIFIED:** approximately `(PrSchBOM StdQty / WO scheduled output) × requested output`, rounded to 4 decimals; scrap/reject overload adds proportional quantities. |
| 8 | Already-issued qty? | **VERIFIED:** aggregated from posted `IvTrxHistory` IP records; legacy keying is not exact operation-line identity. |
| 9 | Outstanding qty? | **NOT VERIFIED as one old authoritative formula.** UI compares requirement/current issue/historical issue; new system should explicitly maintain `Required - NetIssued`. |
| 10 | Partial issue? | **VERIFIED:** yes, multiple issue quantities/lots/documents are possible. |
| 11 | Over-issue? | **VERIFIED:** code comments explicitly permit issue above Work Order schedule in some paths; a tolerance check exists when tolerance is configured. Behavior is inconsistent enough to replace with a single new policy. |
| 12 | Substitutes? | **PARTIALLY VERIFIED:** `BomDefault=0` represents alternative material in WO BOM validation. Normal IP BOM loader filters default rows; complete operator substitution flow in IP page was not verified. |
| 13 | Warehouse/location selection? | **VERIFIED:** BOM warehouse is used to load stock; selected `IvBalLoc` supplies exact warehouse/location/lot. Universal fallback priority not verified. |
| 14 | Available stock? | **VERIFIED:** active `IvBalLoc` quantity; posting revalidates exact stock slice. |
| 15 | UOM conversion? | **PARTIALLY VERIFIED:** standard/catch-weight quantities exist, but old logic is not as explicit as the new UOM chain. New existing UOM service should be authoritative. |
| 16 | Lot/batch handling? | **VERIFIED:** multiple lots supported; candidate rows ordered by stock `TransDate` ascending. |
| 17 | Stock transactions generated? | **VERIFIED:** IP `IvTrxBatch/Detail`, inventory history/cost updates, stock decrease, WIP increase, production daily IP record. |
| 18 | When does stock reduce? | **VERIFIED:** on IP post, not merely when BOM is loaded or document is staged. |
| 19 | WIP or immediate consumption? | **VERIFIED:** WIP. |
| 20 | Material cost? | **VERIFIED:** actual selected stock slice unit price/cost is propagated into inventory/WIP posting. Complete upstream valuation algorithm is not fully verified. |
| 21 | Return from production? | **PARTIALLY VERIFIED:** `SR` return semantics exist and reduce issue in production reporting; full standard factory return chain was not traced to the same depth. |
| 22 | Cancellation/reversal? | **VERIFIED:** posted IP has real rollback restoring stock/WIP/history state; not simple deletion. |
| 23 | Relationship to output? | **VERIFIED:** subsequent daily production moves/consumes WIP and records process/output quantities. |
| 24 | Tied to WC/process? | **VERIFIED:** BOM selection is exact WO + WC + process. Legacy history field mapping weakens exact reconciliation. |
| 25 | Shortages? | **VERIFIED at issue time:** stock vs required/current issue is checked. Automatic feed into MRP/PR from this IP workflow is **NOT VERIFIED**. |
| 26 | Concurrent over-issue prevention? | **OLD:** global posting lock + SQL transaction and stock revalidation. **NEW:** existing row-level locking/conditional decrement is stronger and should be reused. |
| 27 | Old logic to retain? | WO snapshot, process ownership, partial issue, lot trace, WIP staging, real posting/rollback, actual cost linkage. |
| 28 | Old logic to improve? | outstanding/net-issued model, UOM, alternatives, tolerance, identifier clarity, concurrency, separation of UI/service logic. |
| 29 | Existing new tables/services to reuse? | `ProductionWorkOrder*`, especially `ProductionWorkOrderMaterial`; `IvTrxBatch/Detail/History`; `IvBalLoc`; UOM service; inventory posting/repository; `PrProductionPostingLink`. |
| 30 | Genuinely required new components? | Production material execution service, material allocation service, immutable production material movement/line link, Blazor issue UI, return/reversal integration, material execution inquiries. |

---

# Key Source Evidence Catalogue

## Old ERP

### `ProductionPlan/Helper/IssueProdHelper.cs`

Evidence:

- `GetSchBomAllLot(...)` reads `PrSchBOM` and exact WO/WC/process.
- filters `BomDefault=1`, `WIPBomDefault=0`.
- joins active positive `IvBalLoc` stock.
- reads posted `IvTrxHistory` IP quantity.
- `CalculateReqBOM(...)` uses `Math.Round(StdPackSize * reqQutput, 4)`.
- allocation sorts stock rows by `TransDate` ascending.

### `ERPClasses/BL/PrSchBOMBL.cs`

Evidence:

- BOM retrieval by Work Order + Work Center + process.
- pack-size/material ratio derived from scheduled BOM/output quantities.

### `ProductionPlan/ProdPlan/IssueToProductionEx.aspx.cs`

Evidence:

- operator BOM loading and lot allocation.
- staging into `IvTrxBatch` / `IvTrxBatchDetail` type IP.
- warehouse/location/lot capture.
- stock/requirement/historical issue validation.
- tolerance validation.
- post request routed to `ProdNeedPostHelper.PostIP`.

### `ProductionPlan/Helper/IssueProdPostHelper.cs`

Evidence:

- calls inventory IP posting.
- calls `PostIPtoWIPBal`.
- adds `PrSchDailyProd` IP record.
- changes Work Order progress state.
- transactionally updates stock/WIP/production tables.
- rollback reverses inventory/WIP/production history.

### `ERPClasses/Classes/CPosting.cs`

Evidence:

- IP dispatch to `PostIPTransaction`.
- exact source-balance quantity check.
- inventory balance decrease.
- history/cost update.
- WIP balance update.
- rollback support.

### `ERPClasses/BL/ProductionBL.cs`

Evidence:

- comments explicitly define `BomDefault=1` as default and `BomDefault=0` as alternative.
- production validation recognizes alternative BOM usage.

### `ProductionPlan/Helper/DailyProdJONGPostHelper.cs`

Evidence:

- production process output history.
- `WIPItemBalLoc` updates.
- previous-process WIP reduction/movement.
- Work Order progress/status updates.

## New ERP `production`

### `ErpWeb.Model/Entities/Production/ProductionWorkOrderMaterial.cs`

Evidence:

- exact `WorkOrderOperationId` ownership.
- BOM numerator/denominator UOM model.
- `RequiredQty`, `RequiredBaseQty`, `ConversionFactorToBase`.
- `ScrapPercent`, `Tolerance`, `IssueMethod`, `SupplySource`.
- alternate group.
- `IssuedQty`, `ReturnedQty`, `ConsumedQty`, `VarianceQty` marked as rebuildable execution projections.

### `ErpWeb.Core/Production/WorkOrderQuantityCalculator.cs`

Evidence:

```text
RequiredQty
= operation basis
× ComponentQtyPerParent
÷ BomOutputQty
× (1 + ScrapPercent/100)
```

with strict UOM conversion and base-UOM calculation.

### `ErpWeb.Core/Production/WorkOrderSnapshotBuilder.cs`

Evidence:

- copies Product Definition values into frozen Work Order material snapshot.
- resolves alternate/supply-source/route producer.
- assigns material warehouse/location defaults.

### `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

Evidence:

- modern Work Order service usage.
- operation/material hierarchy.
- alternate material substitution in Draft.
- release explicitly does not post inventory.

### `ErpWeb.Core/Inventory/IvTrxConstants.cs`

Evidence:

- `IssueToProduction = "IP"` already defined.

### `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs`

Evidence:

- reusable in-transaction stock-out API and rollback API.

### `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

Evidence:

- current public dispatcher does not yet include IP.
- lower-level stock-out core accepts caller-provided expected transaction type.
- locks/revalidates stock, writes history, audit, and supports rollback.

### `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs`

Evidence:

- `UPDLOCK, HOLDLOCK` for stock balances.
- deterministic exact-balance locking.
- atomic conditional decrement (`StdQty >= qty`).

### `ErpWeb.Model/Entities/Production/ProductionPostingLink.cs`

Evidence:

- already reserved for material/WIP/FG posting idempotency and production↔inventory link.
- do not duplicate this capability.

---

# Final Conclusion

The old ERP's strongest production concept is not its Web Forms screen. It is the transaction model:

```text
Frozen Work Order material requirement
        ↓
process-aware material issue
        ↓
real warehouse stock-out
        ↓
real WIP position
        ↓
actual production/process output
        ↓
return/reversal/traceable cost
```

That concept should be preserved.

The new ERP should **not** clone `IssueToProductionEx.aspx.cs`, `CPosting`, or the old WIP table architecture line-by-line. The current new codebase already has a better Work Order snapshot, material formula, UOM model, alternate model, transaction audit model, idempotency model, and stock concurrency engine.

The missing implementation is the **production material execution layer** that safely connects:

```text
ProductionWorkOrderMaterial
        ↔
production material movement facts
        ↔
IvTrxBatch / IvTrxHistory
        ↔
WIP / consumption / output
```

If this layer is implemented with immutable movement facts, exact operation/material identity, existing SQL Server stock locks, and one transaction across stock + production state, the new ERP will preserve the proven behavior of the old system while eliminating the old system's ambiguity and concurrency weaknesses.

