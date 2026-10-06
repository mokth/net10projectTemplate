# ERP Costing Enhancement Plan — Inventory, Procurement & Sales
## Repository-grounded implementation plan for AI Coding Agent

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Baseline commit reviewed:** `a58c508d8efc3de0bd8b34d7c7c9a3f0d3deecdd`  
**Scope:** Inventory + Procurement + Sales costing only, while preserving existing Production costing integrations and stock-ledger contracts.  
**Plan status:** **APPROVED 10/10 IMPLEMENTATION BLUEPRINT — repository-reviewed corrections incorporated.**

**Approval basis:** the plan has been re-reviewed against the current `production` branch ledger, Inventory, Procurement, Sales, Purchase CN/DN, Production valuation, reporting, and close contracts. The implementation constraints added below are mandatory; an AI Coding Agent must not simplify or omit them.

---

# 1. Purpose

Enhance the current ERP costing implementation so that the same authoritative financial inventory ledger can reliably support:

1. **Moving Weighted Average Cost** — keep and harden the current perpetual moving-average implementation.
2. **FIFO financial costing** — add real financial FIFO layers; do not confuse the existing physical FIFO shipment allocation with costing.
3. **Standard Costing** — add effective-dated standard cost revisions, standard-cost inventory valuation, purchase price variance, and controlled revaluation.
4. **Reliable Procurement actual-cost flow** — GR provisional valuation must be reconciled when Purchase Invoice actual cost arrives.
5. **Reliable Sales COGS and returns** — no duplicate COGS, no broad document/item fallback, and sales return must use exact original cost lineage.
6. **Authoritative costing reports** — Inventory Valuation, COGS/Gross Profit, Purchase Cost/PPV must read the valuation ledger rather than mutable `IvBalLoc.UnitPrice` / `IvStockMaster.PurchasePrice`.
7. **Costing-safe posting validations** — prevent missing FX, unapproved zero/manual costs, invalid count/adjustment costs, and closed/frozen-period writes from corrupting valuation.
8. **The two SHOULD enhancements identified in review**:
   - persistent stock freeze for stock count / protected stock scopes;
   - landed cost allocation after PI variance is stable.

This is a **delta/enhancement plan against the current production branch**, not a request to rebuild the stock ledger from scratch.

---

# 2. Existing foundation that MUST be preserved

The current branch already contains a strong V2 stock/valuation foundation. The coding agent must reuse it.

## 2.1 Existing financial truth

Current files:

- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`
- `ErpWeb.Core/StockLedger/StockPostingContext.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- `ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`
- `ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs`
- `ErpWeb.Model/Entities/StockLedger/StockPosting.cs`
- `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`
- `ErpWeb.Model/Entities/StockLedger/StockValuationPeriodSnapshot.cs`
- `ErpWeb.Model/Configurations/StockLedger/*`
- `scripts/create-stock-valuation-ledger.sql`

The architecture already provides:

```text
Source document
    -> IvTrxBatch / IvTrxBatchDetail
    -> StockPostingCoordinator
    -> physical inventory mutation
    -> IvTrxHistory
    -> InventoryValuationService
    -> StockValuationFact
    -> StockCostState
    -> financial period snapshot
```

This stays the authoritative path.

## 2.2 Existing financial invariants to preserve

Do not weaken these current behaviours:

- `StockValuationFact` is append-only / immutable.
- reversals use `ReversesValuationFactId`; do not recalculate current cost for rollback.
- one posting-line/split identity is unique.
- negative stock cost state is blocked.
- zero quantity with residual value is blocked at close.
- `StockCostState` reconciles to valuation facts.
- period close blocks unresolved `UNVALUED` facts.
- physical quantity snapshot is reconciled to valued quantity.
- posting is tenant/branch scoped.
- source-document/revision/posting-role uniqueness remains.
- request fingerprint / idempotency remains.
- backdated valued movement protection remains.
- no-future-stock validation remains.
- DO / Invoice `LinkDo` physical ownership remains:
  - DO owns stock-out when the DO shipped;
  - `LinkDo == true` invoice does **not** stock-out again;
  - direct `LinkDo == false` invoice owns its own stock-out.

## 2.3 Do NOT use these as historical costing authority

These remain operational/display data only:

- `IvBalLoc.UnitPrice`
- `IvBalLoc.Cost`
- `IvStockMaster.PurchasePrice`

They may be maintained for UI compatibility, but historical costing reports must not be reconstructed from them.

---

# 3. Findings this plan MUST resolve

## MUST enhancements

1. Add a real costing-method policy and strategy seam.
2. Complete Purchase Invoice actual-cost / purchase-price-variance flow.
3. Add **GR-level PI settlement** so partial/multiple GR and partial/multiple PI are deterministic and auditable.
4. Add **financial document costing revision/idempotency** so POST -> rollback -> repost cannot collide with `StockPosting` source/revision uniqueness.
5. Remove foreign-currency PI fallback to exchange rate `1` and centralize company-base-currency resolution across GR, PI and Purchase CN/DN.
6. Replace the current estimated stock-value report as costing authority.
7. Fix Sales Return exact original-cost lineage, especially `Invoice -> LinkDo -> DO`, including returns whose original sale consumed multiple FIFO valuation splits.
8. Remove unsafe broad Invoice COGS fallback when exact line identity is missing.
9. Prevent arbitrary positive Stock Adjustment cost from changing valuation.
10. Correct Stock Count increase valuation policy.
11. Enforce Misc Receipt cost evidence / approved zero cost.
12. Implement true financial FIFO.
13. Implement Standard Costing.
14. Add the **Production actual-cost -> Inventory Standard Cost bridge** so FG Receipt cannot break reconciliation when STANDARD is active.
15. Make cost-method cutover an auditable **old-method OUT -> new-method IN** transfer, never a second opening balance that double-counts inventory.
16. Define warehouse valuation semantics explicitly because the financial cost pool remains `Company + Branch + Item`, not warehouse-specific.
17. Define Purchase CN/DN costing separately for price-only adjustment, debit note, and physical vendor return.
18. Centralize valuation movement classification before the first value-only adjustment is enabled.

## SHOULD enhancements

19. Replace `NoActiveStockFreezeGuard` with persistent stock freeze.
20. Add landed cost through the same immutable cost-adjustment pipeline after PI settlement/variance is stable.

Everything else is out of scope unless required to satisfy one of the above.

# 4. Scope boundaries and non-goals

Do **not** turn this into SAP product costing.

Not in this plan:

- LIFO.
- simultaneous mixed financial methods by item within one branch.
- activity-based costing.
- advanced overhead absorption.
- intercompany transfer pricing.
- full manufacturing standard-cost variance accounting.
- retrospective rewrite of sealed historical valuation facts.
- replacing `IvTrxBatch`, `IvTrxHistory`, or `IvBalLoc`.
- a second stock-posting coordinator.
- a second COGS engine.
- using shipment FIFO as financial FIFO.
- rewriting production WIP/FG architecture unless a regression fix is required because of the shared valuation strategy seam.
- per-item mixed financial costing methods within one branch.
- claiming exact FIFO value by warehouse while FIFO financial layers remain branch/item pooled; warehouse-level FIFO value is an allocation/drill-down, not a separate accounting pool.
- treating Production actual-value variance under STANDARD as inventory value; it must remain an explicit production-standard variance outside inventory value.

Production must continue to pass its existing stock-ledger tests.

---

# 5. Costing policy decision

## 5.1 One active financial method per company + branch

For this implementation phase, use exactly one active financial costing method per:

```text
CompanyCode + BranchCode
```

Allowed methods:

```text
MOVING_AVERAGE
FIFO
STANDARD
```

Do **not** introduce per-item mixed cost methods in this phase.

Why:

- current `StockCostState` pool is company + branch + item;
- period close groups facts by item/method;
- mixed item methods complicate stock close, cutover, audit, and report reconciliation;
- most SME companies select one inventory costing policy per accounting entity/branch.

## 5.2 Effective-dated policy

Create a new entity/table:

### `StockCostPolicyRevision`

Suggested columns:

```text
Id                       bigint PK
CompanyCode              nvarchar(5)
BranchCode               nvarchar(5)
CostMethod               nvarchar(30)
EffectiveFrom            date
EffectiveTo              date NULL
Status                    nvarchar(20)   -- ACTIVE / SUPERSEDED
Reason                    nvarchar(250) NULL
ApprovedBy                nvarchar(100)
ApprovedAtUtc             datetime2(7)
CreatedAtUtc              datetime2(7)
CreatedBy                 nvarchar(100)
RowVersion                rowversion
```

Constraints:

- `CostMethod IN ('MOVING_AVERAGE','FIFO','STANDARD')`
- only one policy may cover a business date for a company/branch;
- no overlapping effective ranges;
- no retroactive policy edit after sealed valuation facts exist in the affected period.

Create:

- `StockCostPolicyRevision.cs`
- `StockCostPolicyRevisionConfiguration.cs`
- `IStockCostMethodResolver`
- `StockCostMethodResolver`

`IStockCostMethodResolver.ResolveAsync(db, company, branch, effectiveAt)` must fail closed if no policy exists.

## 5.3 Migration default

For every existing branch that already has V2 costing:

- seed `MOVING_AVERAGE`;
- `EffectiveFrom` = its V2 ledger epoch/cutover start;
- do not alter existing `StockValuationFact.CostMethod`.

This makes the current system behaviour explicit rather than silently hardcoded.

---

# 6. Refactor costing into method strategies without replacing the ledger

Current:

`ErpWeb.Core/StockLedger/InventoryValuationService.cs`

currently contains moving-average logic directly.

Refactor it into an orchestration service plus method strategies.

Create:

```text
ErpWeb.Core/StockLedger/Costing/
    IInventoryCostingStrategy.cs
    MovingAverageCostingStrategy.cs
    FifoCostingStrategy.cs
    StandardCostingStrategy.cs
    InventoryCostingStrategyResolver.cs
```

Suggested contract:

```csharp
public interface IInventoryCostingStrategy
{
    string CostMethod { get; }

    Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> pendingHistory,
        CancellationToken cancellationToken);
}
```

`InventoryValuationService` remains the registered `IInventoryValuationService` and performs:

1. load policy for `context.Posting.EffectiveAt`;
2. load pending history/evidence exactly as today;
3. select one costing strategy;
4. execute it inside the existing coordinator transaction;
5. save valuation facts;
6. update cost state / FIFO layers / standard state;
7. never seal itself; sealing remains `StockPostingCoordinator` responsibility.

### Moving Average strategy

Move the current proven WAC logic into `MovingAverageCostingStrategy` with behavioural parity.

Do not change the mathematical results while refactoring.

### `StockCostMethods`

Extend:

```csharp
public const string MovingAverage = "MOVING_AVERAGE";
public const string Fifo = "FIFO";
public const string Standard = "STANDARD";
```

---

# 7. Harden `StockCostState` for multiple methods

Current entity:

`ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`

contains `StockCostState` with:

```text
OnHandBaseQty
InventoryValue
AverageUnitCost
```

`AverageUnitCost` is WAC-specific.

Add:

```text
CurrentUnitCost decimal(19,6)
```

Migration:

```text
CurrentUnitCost = AverageUnitCost
```

Rules:

- WAC:
  - `CurrentUnitCost == AverageUnitCost`.
- FIFO:
  - `CurrentUnitCost` = `InventoryValue / OnHandBaseQty` informational current blended value;
  - FIFO issue cost comes from FIFO layers, never this field.
- STANDARD:
  - `CurrentUnitCost` = effective standard cost.
- keep `AverageUnitCost` for compatibility in this plan; deprecate it outside WAC but do not remove it.

Every report/service created by this plan must use `CurrentUnitCost` when it needs a current informational unit cost.

---

# 8. Phase A — Procurement actual cost / Purchase Invoice variance

## 8.1 Existing correct GR behaviour to preserve

File:

`ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`

The current GR path freezes commercial cost evidence from the PO and converts it to company base currency/base stock UOM. Preserve that evidence and the existing Inventory posting path.

```text
PO net purchase amount
    -> purchase UOM unit cost
    -> approved transaction-date FX
    -> company base currency
    -> base stock UOM
    -> GR valuation fact
```

For WAC/FIFO, GR is the initial/provisional receipt valuation. For STANDARD, the commercial amount remains evidence but the Inventory receipt is valued at effective standard cost.

## 8.2 Centralize company currency / FX resolution

Current repo facts to respect:

- `PoInvoice.CurrRate` defaults to `1m` today.
- `PoInvoiceService.ResolveCurrRateAsync()` can silently return `1m`.
- `PoCdnService` already has a stricter fail-closed resolver, but it currently identifies MYR directly instead of using the company's configured base currency.
- GR already requires approved FX evidence.

Create one service and reuse it everywhere:

```text
ICompanyCurrencyRateResolver
CompanyCurrencyRateResolver
```

Suggested contract:

```csharp
Task<CurrencyRateResult> ResolveAsync(
    AppDbContext db,
    string companyCode,
    string transactionCurrency,
    DateTime businessDate,
    CancellationToken cancellationToken);
```

Rules:

```text
Company.CurrencyCode == transaction currency
    -> rate = 1

Foreign currency
    -> active SaCurrRate window covering business date is mandatory
    -> HomeCurPerUnit > 0
    -> missing/invalid rate => fail SAVE/POST
```

Migrate GR, PI and Purchase CN/DN to the shared resolver. Do not hard-code `MYR` and do not silently default foreign currency to `1`.

## 8.3 Add exact GR-level PI settlement

A PO line can have multiple GRs at different dates/rates/costs and multiple PIs. `PoInvoiceDetail` only contains the PO link; it does not identify which GR valuation quantities the PI settles. Therefore PI variance must not be calculated from one averaged PO-line assumption.

Create immutable settlement rows:

### `PurchaseReceiptCostSettlement`

```text
Id                         bigint PK
CompanyCode                nvarchar(5)
BranchCode                 nvarchar(5)

PiDocNo                    nvarchar(30)
PiLineNo                   smallint
PiCostingRevision          int

PoNo                       nvarchar(50)
PoRelNo                    smallint
PoLineNo                   smallint
ItemCode                   nvarchar(30)

ReceiptValuationFactId     bigint
ReceiptInventoryHistoryId  int NULL
ReceiptBatchNo             int NULL

SettledBaseQty             decimal(19,6)
ReceiptReferenceUnitCost   decimal(19,6)
ProvisionalBaseAmount      decimal(19,6)
AllocatedActualBaseAmount  decimal(19,6)
VarianceAmount             decimal(19,6)

StockPostingId             bigint
ReversesSettlementId       bigint NULL
CreatedAtUtc               datetime2(7)
CreatedBy                  nvarchar(100)
```

Required FKs/indexes:

- FK `ReceiptValuationFactId -> StockValuationFact.Id`.
- FK `StockPostingId -> StockPosting.Id`.
- FK `ReversesSettlementId -> PurchaseReceiptCostSettlement.Id`.
- unique reversal link where `ReversesSettlementId IS NOT NULL`.
- index `(CompanyCode, BranchCode, PoNo, PoRelNo, PoLineNo, ReceiptValuationFactId)`.
- index `(CompanyCode, BranchCode, PiDocNo, PiCostingRevision, PiLineNo)`.

Settlement candidate receipt facts:

1. same company/branch;
2. GR-origin valued receipt;
3. exact `PoNo + PoRelNo + PoLineNo + ItemCode` from linked `IvTrxHistory`;
4. sealed posting;
5. original receipt fact is still active (not exactly reversed);
6. remaining unsettled quantity > 0.

Deterministic allocation order:

```text
GR BusinessDate
-> GR StockPosting.PostingSequence
-> ReceiptValuationFactId
```

For each PI line, allocate `StdQty` across receipt facts until fully settled. A PI line may settle several GR facts; one GR fact may be settled by several PI lines/documents over time.

For each settlement slice:

```text
available receipt qty
    = receipt fact BaseQty
      - active settled qty against that fact

AllocatedActualBaseAmount
    = PI actual base line amount * slice qty / PI line base qty

ProvisionalBaseAmount
    = receipt fact cost represented by slice qty

VarianceAmount
    = AllocatedActualBaseAmount - ProvisionalBaseAmount
```

Rounding rules:

- allocate actual amount proportionally; final settlement slice receives the residual so slices equal PI actual line amount exactly;
- when the last unsettled quantity of a receipt fact is settled, consume the exact remaining provisional receipt value to avoid residual cents/value;
- rollback appends reversal settlement rows; never update/delete original rows.

If the PI quantity cannot be fully mapped to active GR receipt facts, POST fails. Do not guess from current purchase price or PO price.

## 8.4 Add immutable purchase cost adjustment audit

Create:

### `PurchaseCostAdjustment`

```text
Id                         bigint PK
CompanyCode                nvarchar(5)
BranchCode                 nvarchar(5)
AdjustmentType             nvarchar(30)
SourceDocumentType         nvarchar(30)
SourceDocumentNo           nvarchar(50)
SourceDocumentLine         int
SourceCostingRevision      int
PoNo                       nvarchar(50) NULL
PoRelNo                    smallint NULL
PoLineNo                   smallint NULL
ItemCode                   nvarchar(30)
CostMethod                 nvarchar(30)

BaseQty                    decimal(19,6)
ActualBaseAmount           decimal(19,6)
ReferenceBaseAmount        decimal(19,6)
TotalAdjustmentAmount      decimal(19,6)
InventoryAdjustmentAmount  decimal(19,6)
ConsumedVarianceAmount     decimal(19,6)

StockPostingId             bigint
InventoryAdjustmentFactId  bigint NULL
ReversesAdjustmentId       bigint NULL
CreatedAtUtc               datetime2(7)
CreatedBy                  nvarchar(100)
```

Types:

```text
PI_VARIANCE
PURCHASE_CN_PRICE_ADJUSTMENT
PURCHASE_DN_PRICE_ADJUSTMENT
PURCHASE_RETURN_VARIANCE
LANDED_COST
```

Never update/delete a posted row. Reversal creates a linked compensating adjustment.

## 8.5 Add financial-document costing revision / idempotency

This is mandatory because current `StockPosting` uniquely keys:

```text
CompanyCode + BranchCode + SourceModule + SourceDocumentType
+ SourceDocumentId + DocumentRevision + PostingRole
```

while current `PoInvoice` and `PoCdn` have no financial posting revision.

Add:

```text
PoInvoice.CostingRevision int NOT NULL default 0
PoCdn.CostingRevision     int NOT NULL default 0
```

Future landed-cost documents must also contain `CostingRevision`.

Contract:

```text
first financial POST
    -> PRIMARY revision N

ROLLBACK
    -> REVERSAL revision N, ReversesPostingId = exact PRIMARY posting
    -> after successful reversal, document CostingRevision increments to N + 1

repost, edited or unchanged
    -> PRIMARY revision N + 1
```

This makes POST -> rollback -> repost compatible with existing `StockPosting` uniqueness and request idempotency.

The request ID/fingerprint must include at least source type/no, costing revision, posting role, effective date, and immutable commercial/settlement snapshot hash.

## 8.6 Concrete procurement financial posting seam

Do not let `PoInvoiceService` or `PoCdnService` directly hand-code `StockValuationFact` and `StockCostState` mutations in many places.

Create:

```text
PurchaseCostPostingCommandFactory
IPurchaseCostAdjustmentPostingService
PurchaseCostAdjustmentPostingService
IStockValueAdjustmentWriter
StockValueAdjustmentWriter
```

Responsibilities:

### `PurchaseCostPostingCommandFactory`

Creates the existing `StockPostingCommand` envelope with:

```text
SourceModule       = PROCUREMENT
SourceDocumentType = PO_INVOICE / PO_CDN / LANDED_COST
SourceDocumentId   = stable document identity
DocumentRevision   = CostingRevision
PostingRole        = PRIMARY / REVERSAL
```

### `StockValueAdjustmentWriter`

The one shared writer for zero-quantity financial value adjustments. It must:

- run only inside an unsealed `StockPostingContext`;
- append `StockValuationFact` with `BaseQty = 0` and the correct direction/value/method;
- update the active method state (WAC state / FIFO layer adjustment / Standard variance state where applicable);
- never mutate sealed facts;
- enforce non-negative resulting Inventory value;
- use deterministic `PostingLineNo + SplitOrdinal` identities.

It is a writer into the existing ledger, **not another ledger or coordinator**.

### `PurchaseCostAdjustmentPostingService`

Owns PI variance, Purchase CN/DN financial adjustment, Landed Cost, and their exact reversals.

## 8.7 PI posting must join one transaction boundary

Enhance `PoInvoiceService.PostOneAsync()` so the commercial document and financial adjustment are atomic:

```text
begin SQL transaction
    -> acquire existing branch stock/costing lock
    -> lock PI
    -> lock related PO rows in deterministic order
    -> validate status/qty/UOM/price/FX
    -> allocate exact GR settlement slices
    -> begin StockPosting context for PI CostingRevision
    -> append PurchaseReceiptCostSettlement rows
    -> calculate method-specific variance
    -> append PurchaseCostAdjustment rows
    -> append value-only StockValuationFact(s) where inventory value changes
    -> update StockCostState / FIFO layers
    -> update PO InvoicedQty
    -> complete/seal StockPosting
    -> mark PI POSTED
    -> SaveChanges
commit
```

Any failure rolls back every commercial, settlement and costing mutation.

Rollback must reverse the exact settlement/adjustment posting before restoring PI to NEW, then increment `CostingRevision` for the next repost generation.

## 8.8 PI actual base amount

Use existing commercial fields:

```text
PoInvoiceDetail.NetAmount
PoInvoiceDetail.StdQty
PoInvoice.CurrRate
```

For a stock-controlled line:

```text
ActualBaseLineAmount = NetAmount * CurrRate
ActualBaseUnitCost   = ActualBaseLineAmount / StdQty
```

Tax treatment follows the existing `NetAmount` contract. Do not capitalize `TaxAmt` unless the ERP later explicitly marks the tax as non-recoverable.

Use centralized quantity/money/unit-cost/rate precision helpers; do not use `IvQty.Round` as a universal monetary routine.

## 8.9 WAC PI variance policy

Do not rewrite GR facts and do not backdate PI adjustment to GR date. PI variance is effective on PI financial posting/business date.

Settlement supplies exact provisional amount; WAC still intentionally has no receipt-layer ownership after commingling, so the split between current Inventory and consumed variance is a documented current-period policy, not a claim of exact physical receipt identity.

Calculate settlement variance:

```text
TotalVariance
    = sum settlement AllocatedActualBaseAmount
      - sum settlement ProvisionalBaseAmount
```

For each **item across the whole PI posting**, establish one capitalization quantity budget:

```text
SettledQtyForItem
    = sum PI-settled base qty for item in this posting

CapitalizableQtyBudget
    = min(current positive StockCostState.OnHandBaseQty,
          SettledQtyForItem)
```

Allocate that quantity budget across the item's settlement slices in deterministic PI-line / settlement order. Do **not** apply `min(line qty, on-hand)` independently to every line, because repeated same-item lines could capitalize more quantity than is on hand.

For each slice:

```text
InventoryShare          = allocated capitalizable slice qty / slice settled qty
InventoryAdjustment     = slice variance * InventoryShare
ConsumedVariance        = slice variance - InventoryAdjustment
```

Rules:

- sum item-level capitalized quantity <= current item on-hand;
- positive and negative variance supported;
- resulting Inventory value may never be negative;
- if on-hand = 0, all variance is consumed variance;
- already sealed historical sales COGS is not rewritten;
- inventory portion appends value-only fact(s) and recomputes WAC from resulting state;
- consumed variance stays in immutable `PurchaseCostAdjustment` for management/GL bridge reporting;
- every item satisfies `TotalVariance = InventoryAdjustment + ConsumedVariance` exactly after residual allocation.

## 8.10 FIFO PI variance policy

FIFO has receipt layers, so the settlement can identify the exact originating GR valuation fact(s).

For each settlement slice:

1. find FIFO layer(s) whose `OriginValuationFactId` is the settlement receipt fact;
2. determine remaining open quantity/value on those layers;
3. apply the proportional variance to the still-open quantity as immutable value adjustment;
4. update `AccumulatedAdjustment`, `RemainingValue`, and `CurrentUnitCost` for open layer quantity;
5. classify the portion attributable to already-consumed layer quantity as consumed variance;
6. do not rewrite old FIFO issue facts.

Exact reconciliation:

```text
settlement VarianceAmount
=
InventoryAdjustmentAmount
+
ConsumedVarianceAmount
```

If one receipt fact generated multiple layer rows, allocation is deterministic by layer ID.

## 8.11 Standard Cost PI variance policy

Under STANDARD:

- Inventory remains at approved effective standard cost;
- PI actual cost never revalues Inventory;
- for each settlement slice:

```text
ReferenceBaseAmount
    = SettledBaseQty * effective standard cost for PI business date

PPV
    = AllocatedActualBaseAmount - ReferenceBaseAmount

InventoryAdjustmentAmount = 0
ConsumedVarianceAmount     = PPV
```

Persist the GR/provisional settlement evidence as well as standard-reference amount so Procurement inquiry can show PO/GR/PI/standard differences without changing Inventory value.

## 8.12 Purchase CN/DN costing contract

Current `PoCdnService` separates supplier financial adjustment from optional physical Vendor Return. Preserve that distinction.

### Case A — price-only Purchase Credit Note (`ReturnStock == false`)

- no physical stock movement;
- reference exact active PI settlement(s) / invoice line(s);
- create **negative** purchase cost adjustment;
- WAC: apply the same item-level Inventory-vs-consumed allocation policy as a negative PI variance;
- FIFO: reduce open originating layer values where possible; consumed portion is variance;
- STANDARD: Inventory unchanged; record negative PPV/price variance.

### Case B — Purchase Debit Note

- no physical stock movement unless a separate supported flow explicitly exists;
- reference exact PI settlement when line-based;
- create **positive** purchase cost adjustment;
- method handling mirrors positive PI variance.

### Case C — Purchase Credit Note with physical return (`ReturnStock == true`)

Two distinct financial effects occur:

```text
1. Vendor Return batch
   -> physical stock-out
   -> valued by active WAC/FIFO/STANDARD method

2. Supplier credit amount
   -> purchase financial adjustment
   -> linked to exact PI settlement/reference
```

Do not value the Vendor Return from `PoCdnDetail.CostPrice`; current repo already documents that field as informational only.

The supplier credit and physical stock-out can differ in value. Persist the difference as `PURCHASE_RETURN_VARIANCE`; do not force the physical stock-out fact to equal supplier credit.

All three cases require exact rollback and `CostingRevision` generation handling.

# 9. Phase B — Manual receipt / adjustment / stock-count cost gates

## 9.1 Misc Receipt

File:

`ErpWeb.Core/Inventory/IvMiscReceiptService.cs`

Current risk:

- unit price may be zero;
- `PriceConfirmed` exists but financial valuation can still resolve from `UnitPrice`.

Change the posting contract.

For every stock-in MR line, persist one explicit evidence type:

```text
MANUAL_APPROVED
ZERO_COST_APPROVED
OPENING_APPROVED
```

Add fields to `IvTrxBatchDetail` if necessary:

```text
CostEvidenceType
CostOverrideReason
CostApprovedBy
CostApprovedAtUtc
```

Do not encode all semantics into a free-text `PriceEvidence` string.

Rules:

- cost > 0:
  - requires `MANUAL_APPROVED`;
- cost == 0:
  - requires explicit `ZERO_COST_APPROVED`;
  - reason mandatory;
- opening migration:
  - requires `OPENING_APPROVED`;
- no confirmed evidence:
  - block POST.

WAC/FIFO uses the approved cost.
STANDARD ignores entered valuation for inventory and values at effective standard; the entered amount remains commercial/reference evidence.

## 9.2 Positive Stock Adjustment

File:

`ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`

Current risk:

```text
UnitPrice >= 0
```

is insufficient.

New rules:

### WAC

For positive quantity:

- default cost must be current authoritative WAC from `StockCostState`;
- user-entered different cost requires approved manual override;
- if no cost state / zero existing valued quantity:
  - explicit opening/manual approved cost mandatory.

For negative quantity:

- ignore entered financial price;
- issue at authoritative current WAC.

### FIFO

Positive adjustment:

- create a new adjustment FIFO layer;
- approved cost evidence mandatory unless a documented system-derived source exists.

Negative adjustment:

- consume FIFO layers exactly like a normal financial issue.

### STANDARD

Positive/negative adjustment:

- value at effective standard cost;
- entered price cannot change inventory cost;
- differences may be retained as audit/reference only.

## 9.3 Stock Count

File:

`ErpWeb.Core/Inventory/IvStockCountService.cs`

Remove costing authority from:

```text
balance.UnitPrice ?? master.PurchasePrice ?? 0
```

for the final posted financial adjustment.

`SnapshotUnitPrice` may remain informational evidence for the count sheet, but must not control authoritative financial value.

At post time:

### WAC

Count gain:
- use current WAC;
- if no valued cost exists, require approved manual/opening cost before POST.

Count loss:
- use current WAC.

### FIFO

Count gain:
- create approved count-adjustment FIFO layer at approved/system-derived cost.

Count loss:
- consume FIFO layers.

### STANDARD

Gain/loss:
- use effective standard cost.

The stock count must not silently alter method policy.

---

# 10. Phase C — Sales COGS and Sales Return correctness

## 10.1 Preserve COGS ownership contract

Files:

- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`

Hard invariant:

```text
DO physical stock-out
    -> DO valuation owner

Invoice LinkDo == true
    -> no second physical stock-out
    -> no second COGS
    -> report resolves source DO valuation

Direct Invoice LinkDo == false
    -> Invoice valuation owner
```

Do not change this.

## 10.2 Remove unsafe invoice COGS fallback

File:

`ErpWeb.Core/StockLedger/StockValuationQueryService.cs`

Current behaviour:

- attempts exact source line;
- if no exact line, falls back to all matching document + item facts.

Change:

```text
exact owner type + document + source line + item found
    -> resolved

exact source line missing or ambiguous
    -> IsResolved = false
    -> COGS must not be silently guessed
```

Never aggregate all same-item facts onto each invoice line.

For authoritative gross-profit reports:

- unresolved COGS marks the invoice/report row as unresolved;
- totals must separately expose unresolved count/value;
- period close/reconciliation may block if sales physical stock-out lacks deterministic line ownership.

## 10.3 Fix Customer Return original-cost lineage with multi-fact allocation

Files:

- `ErpWeb.Core/Sales/SaCdnService.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- relevant sales/inventory entities/configurations.

Current risk:

`SaCdnService` primarily carries Invoice identity while the original physical COGS owner may be a DO. In addition, under FIFO one sales line can produce multiple `StockValuationFact` splits, so one `OriginalValuationFactId` is not enough for a partial return.

Persist convenient source-owner fields on return batch/history when useful:

```text
OriginalOwnerType
OriginalOwnerDocumentNo
OriginalOwnerDocumentLine
```

But make the authoritative return-cost link a separate immutable allocation table.

### `SalesReturnCostAllocation`

```text
Id                         bigint PK
CompanyCode                nvarchar(5)
BranchCode                 nvarchar(5)
ReturnDocumentType         nvarchar(30)
ReturnDocumentNo           nvarchar(50)
ReturnDocumentLine         int
ReturnCostingRevision      int

OriginalValuationFactId    bigint
OriginalOwnerType          nvarchar(30)
OriginalOwnerDocumentNo    nvarchar(50)
OriginalOwnerDocumentLine  nvarchar(50) NULL

ReturnedBaseQty            decimal(19,6)
ReturnedCostAmount         decimal(19,6)
StockPostingId             bigint
ReturnValuationFactId      bigint NULL
ReversesAllocationId       bigint NULL
CreatedAtUtc               datetime2(7)
CreatedBy                  nvarchar(100)
```

Rules:

- FK to exact original outbound `StockValuationFact`.
- one return line may have many allocation rows.
- one original outbound fact may be returned by multiple CNs until its quantity is exhausted.
- active returned quantity against an original fact = primary allocations not exactly reversed.
- cumulative returned qty must never exceed original `BaseQty`.
- rollback appends reversal allocation rows; originals remain immutable.

### Original outbound selection

Resolve the sales valuation owner first:

```text
Invoice LinkDo == true
    -> owner = exact DO / DO line

Direct Invoice
    -> owner = exact Invoice / Invoice line
```

Then load all sealed active SALE_OUT valuation facts for that exact owner line and item, ordered by:

```text
PostingLineNo
-> SplitOrdinal
-> Fact Id
```

Allocate return qty across remaining returnable facts in that deterministic order.

Example FIFO sale:

```text
DO line qty 150
  fact split 0: 100 @ 10
  fact split 1:  50 @ 12

return qty 120
  allocation A: 100 against split 0 = 1,000
  allocation B:  20 against split 1 =   240
  exact original return basis        = 1,240
```

Do not collapse this into one guessed unit cost.

### WAC sales return

Each return allocation reverses the exact original outbound cost represented by its original fact. The inventory receipt at return date is the sum of those allocation costs and becomes a new WAC receipt.

### FIFO sales return

Create new FIFO return layer(s) at the return posting date from the exact allocation costs. Do not reopen old consumed layers.

For each allocation:

```text
Return layer qty   = ReturnedBaseQty
Return layer value = ReturnedCostAmount
OriginalValuationFactId = allocation original fact
```

### STANDARD sales return

COGS reversal uses exact original outbound allocation facts.

Inventory comes back at **current effective standard cost**. If current standard differs from original COGS reversal basis, persist the difference as an explicit standard-return variance outside Inventory value:

```text
CurrentStandardReceiptValue
-
ExactOriginalCogsReversalValue
=
ReturnStandardVariance
```

Do not hide this difference inside Inventory valuation.

## 10.4 Sales CN without physical return

If `ReturnStock == false`:

- no stock valuation fact;
- no inventory receipt;
- financial sales credit affects sales revenue only;
- COGS is unchanged unless a separate physical return is posted.

Preserve this distinction in Gross Profit reporting.

---

# 11. Phase D — Authoritative costing reports

## 11.1 Retire `Est. Inventory Value` as costing authority

Current files:

- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor`
- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor.cs`
- `ErpWeb.Core/Inventory/IvStockSummaryService.cs`
- `ErpWeb.Model/Repositories/Inventory/IvStockInquiryRepository.cs`
- `ErpWeb/Inventory/IvStockValueExportEndpoints.cs`

Current estimated formula:

```text
IvBalLoc.StdQty *
(IvBalLoc.UnitPrice ?? IvStockMaster.PurchasePrice ?? 0)
```

This may stay only as an operational estimate if desired, but it cannot be the official costing report.

## 11.2 Create valuation report service

Create:

```text
ErpWeb.Core/StockLedger/StockValuationReportService.cs
ErpWeb.Core/StockLedger/IStockValuationReportService.cs
```

Queries must use:

- sealed `StockValuationFact`;
- latest applicable `StockValuationPeriodSnapshot` for closed periods;
- facts after snapshot watermark for open/current period;
- never mutable current purchase price as historical fallback.

Support:

```text
AsOfDate
ItemCode
WarehouseCode
ItemClass
CostMethod
GroupBy:
    Item
    Warehouse
    Class
    ItemWarehouse
```

Rows:

```text
Item
Description
Class
Warehouse
BaseUom
Qty
InventoryValue
UnitCost
CostMethod
ValuationStatus
```

For a group with mixed UOM, do not show a meaningless total quantity.

## 11.2A Financial pool versus warehouse valuation semantics

The active financial costing pool in this plan remains:

```text
CompanyCode + BranchCode + ItemCode + CostMethod
```

It is **not** a warehouse-specific accounting pool.

Therefore authoritative branch/item totals come from sealed valuation facts/snapshots. Warehouse reporting is a drill-down/allocation of that branch/item financial total, not an independent costing engine.

### WAC / STANDARD warehouse allocation

For an as-of date:

1. obtain physical as-of quantity by warehouse using the existing stock-history/snapshot contract;
2. obtain authoritative branch/item Inventory value from valuation facts/snapshot;
3. use branch/item WAC or Standard unit cost to allocate value to warehouse quantities;
4. apply any rounding residual to the deterministic final warehouse row so:

```text
sum warehouse allocated value
=
authoritative branch/item Inventory value
```

### FIFO warehouse allocation

FIFO financial layers are branch/item pooled. Transfers do not create new financial layers, and value-only PI/landed-cost adjustments do not naturally belong to one warehouse.

Therefore, unless a future phase changes the financial pool to warehouse-specific FIFO, the report must label warehouse FIFO value as **Allocated Warehouse Value** and must not claim it is exact remaining FIFO-layer ownership by warehouse.

Authoritative FIFO accounting remains the branch/item total and open financial layers.

### Report labels / totals

- Branch/item Inventory Value = authoritative accounting value.
- Warehouse Value = allocated drill-down of that value.
- quantity can be totaled only for compatible UOM.
- all warehouse allocations must reconcile exactly back to branch/item value.

## 11.3 Rewire Inventory Value UI

Reuse the existing route/menu/permission if possible so access control remains stable.

Change page title only after the authoritative service is wired:

```text
Inventory Valuation
```

The export endpoint `/inventory/valuation/export` must use the same report service and the same source-of-truth query as the screen.

The screen, summary cards and export must reconcile exactly.

## 11.4 Minimum Procurement costing inquiry

Create a read model/service for:

```text
PO provisional cost
GR receipt cost
PI actual cost
PI variance
inventory-capitalized variance
consumed variance
landed cost (Phase I)
```

Suggested service:

```text
IPurchaseCostInquiryService
PurchaseCostInquiryService
```

Minimum report grain:

```text
Vendor + PO + PO line + item + PI + PI line
```

## 11.5 Minimum Sales gross profit inquiry

Create a read model/service that combines:

- posted invoice net sales;
- exact COGS from `StockValuationFact`;
- `LinkDo` source DO cost;
- direct invoice cost;
- stock-return COGS reversals when appropriate.

Fields:

```text
Invoice
Date
Customer
Item
Qty
NetSales
Cogs
GrossProfit
GrossMarginPercent
CogsOwnerType
CogsOwnerNo
CogsResolved
```

Formula:

```text
GrossProfit = NetSales - COGS

GrossMargin% =
GrossProfit / NetSales * 100
```

Do not include unresolved lines in a falsely precise "final" total.

---

# 12. Phase E — True financial FIFO

The current `IvSpFifoEligibility` / shipment allocation code is physical stock selection only.

Do not reuse `IvBalLoc` as the financial FIFO layer table.

## 12.1 Add FIFO layer tables

### `StockFifoLayer`

Suggested columns:

```text
Id                        bigint PK
CompanyCode               nvarchar(5)
BranchCode                nvarchar(5)
ItemCode                  nvarchar(30)
BaseUom                   nvarchar(10)

OriginValuationFactId     bigint
OriginStockPostingId      bigint
ReceiptEffectiveAt        datetime2(7)

OriginalQty               decimal(19,6)
RemainingQty              decimal(19,6)

OriginalValue             decimal(19,6)
AccumulatedAdjustment     decimal(19,6)
RemainingValue            decimal(19,6)
CurrentUnitCost           decimal(19,6)

SourceDocumentType        nvarchar(40)
SourceDocumentNo          nvarchar(50)
SourceDocumentLine        nvarchar(50) NULL

WarehouseCode             nvarchar(20) NULL  -- provenance only
LotId                     int NULL            -- provenance only
LotNo                     nvarchar(50) NULL

Status                     nvarchar(20)       -- OPEN / CLOSED
RowVersion                 rowversion
```

Financial FIFO ordering:

```text
ReceiptEffectiveAt
then OriginValuationFactId
```

The financial pool remains company + branch + item.

Do not force the financial layer order to equal physical shipment-lot selection.

### `StockFifoLayerConsumption`

```text
Id                     bigint PK
CompanyCode
BranchCode
IssueValuationFactId   bigint
FifoLayerId            bigint
ConsumedQty            decimal(19,6)
ConsumedValue          decimal(19,6)
SplitOrdinal           int
ReversesConsumptionId  bigint NULL
CreatedAtUtc
CreatedBy
```

Unique:

```text
IssueValuationFactId + SplitOrdinal
```

## 12.2 FIFO receipt

For every valued inventory receipt:

1. create normal receipt `StockValuationFact`;
2. create one new FIFO layer;
3. `OriginalQty = RemainingQty = receipt qty`;
4. `OriginalValue = RemainingValue = receipt value`.

PI variance does not rewrite the receipt fact.

It updates layer adjustment state by adding an immutable adjustment fact.

## 12.3 FIFO issue

For required issue quantity:

```text
lock OPEN FIFO layers for item
order by ReceiptEffectiveAt, Id
consume until requested qty fulfilled
```

For each layer take:

```text
ConsumedQty
ConsumedValue
```

Create valuation fact split(s) or one parent fact with deterministic split identity according to the existing `PostingLineNo + SplitOrdinal` contract.

Recommendation:

- one `StockValuationFact` per layer consumption split;
- `SplitOrdinal` corresponds to FIFO consumption order.

Final layer depletion must consume exact remaining layer value.

No residual value may remain on zero-qty layer.

## 12.4 FIFO reversal

Never rerun FIFO.

Load original `StockFifoLayerConsumption` rows and restore exactly:

```text
same layer
same quantity
same value
```

Create reversal consumption records and valuation facts linked to originals.

## 12.5 FIFO transfer

Internal stock transfer does not create a new financial FIFO receipt layer.

The same financial inventory remains owned by the branch/item pool.

Transfer valuation facts must remain value-neutral.

## 12.6 FIFO sales return

Create a new return layer at return date with original sale cost lineage.

## 12.7 FIFO close reconciliation

Add checks:

```text
sum OPEN RemainingQty
    == StockCostState.OnHandBaseQty

sum OPEN RemainingValue
    == StockCostState.InventoryValue

every FIFO issue fact
    == sum linked layer consumption values
```

Block close on mismatch.

---

# 13. Phase F — Standard Costing

## 13.1 Add effective-dated standard cost

Create:

### `ItemStandardCostRevision`

```text
Id                  bigint PK
CompanyCode         nvarchar(5)
BranchCode          nvarchar(5)
ItemCode            nvarchar(30)

EffectiveFrom       date
EffectiveTo         date NULL

MaterialCost        decimal(19,6)
LabourCost          decimal(19,6) default 0
MachineCost         decimal(19,6) default 0
OverheadCost        decimal(19,6) default 0
SubcontractCost     decimal(19,6) default 0

TotalStandardCost   decimal(19,6)

Status              nvarchar(20) -- APPROVED / SUPERSEDED
Revision            int
ApprovedBy          nvarchar(100)
ApprovedAtUtc       datetime2(7)
Reason              nvarchar(250)
CreatedAtUtc
CreatedBy
RowVersion
```

For the Inventory/Procurement/Sales scope of this plan:

- `MaterialCost` is required.
- other components are retained as zero-capable fields because Production already exists and future integration must not require a breaking schema redesign.
- this plan does not calculate manufacturing activity rates.

Constraints:

```text
TotalStandardCost >= 0
no overlapping effective ranges for same branch/item
```

STANDARD posting fails when a stock-controlled item has no approved standard cost effective on the transaction date.

## 13.2 Standard receipt

Inventory receipt value:

```text
ReceiptQty * effective TotalStandardCost
```

Commercial purchase cost remains separately recorded.

For GR:

- stock valuation = standard;
- PO provisional / supplier amount remains purchase evidence.

## 13.3 Standard issue / COGS

Any issue:

```text
IssueQty * effective standard cost
```

Sales COGS uses the standard cost fact produced by the stock-out owner.

## 13.4 PI Purchase Price Variance

For PI:

```text
Actual purchase base amount
-
PI Qty * effective standard cost
=
PPV
```

No inventory revaluation from PI.

Post PPV as financial variance data/fact, not stock quantity.

## 13.5 Standard cost revision / revaluation

When a new standard becomes effective and stock exists:

```text
RevaluationAmount
=
OnHandQty * (NewStandard - OldStandard)
```

Create a dedicated `StockPosting` with role/type such as:

```text
STANDARD_COST_REVALUATION
```

Append zero-quantity valuation fact(s):

```text
MovementCode = STANDARD_REVALUE_IN
or
MovementCode = STANDARD_REVALUE_OUT
```

Update `StockCostState.InventoryValue` and `CurrentUnitCost`.

Do not modify old valuation facts.

A standard revision may not be backdated into a closed period.

## 13.6 Standard return

As defined in Sales phase:

- reverse original COGS using original lineage;
- inventory receipt at current effective standard;
- difference recorded as explicit return-standard variance.

---


## 13.7 Production actual-cost -> Inventory Standard Cost bridge

This repo already has Production balances/movements and Finished Good Receipt carrying actual production value. STANDARD is branch-wide in this plan, so FG Receipt requires an explicit bridge; otherwise Production can transfer actual value while Inventory records standard value and the difference disappears.

Preserve current Production actual-cost evidence. Do not rewrite Production WIP/FG pooling to standard cost in this plan.

Create immutable variance evidence:

### `ProductionStandardCostVariance`

```text
Id                         bigint PK
CompanyCode                nvarchar(5)
BranchCode                 nvarchar(5)
StockPostingId             bigint
ProductionPostingLinkId    bigint NULL
FinishedGoodReceiptId      bigint NULL
InventoryValuationFactId   bigint
ItemCode                   nvarchar(30)
BaseQty                    decimal(19,6)
ActualProductionValue      decimal(19,6)
StandardInventoryValue     decimal(19,6)
VarianceAmount             decimal(19,6)  -- Actual - Standard
EffectiveAt                datetime2(7)
ReversesVarianceId         bigint NULL
CreatedAtUtc               datetime2(7)
CreatedBy                  nvarchar(100)
```

STANDARD FG rule:

```text
Production actual transferred value
    = existing exact production / FG evidence

Inventory receipt value
    = FG BaseQty * effective standard cost

ProductionStandardVariance
    = ActualProductionValue - StandardInventoryValue
```

Reconciliation invariant:

```text
ActualProductionValue
=
StandardInventoryValue
+
VarianceAmount
```

Important:

- `ProductionStandardCostVariance` is **not** an Inventory value fact; adding it to Inventory would incorrectly return Inventory to actual cost.
- WAC/FIFO keep today's actual production transfer into Inventory and variance is zero/not applicable.
- STANDARD FG posting fails if no approved standard cost exists.
- exact rollback creates linked variance reversal and exact Inventory fact reversal.
- management reporting may expose this as Production Standard Variance; full manufacturing variance accounting/GL is outside this plan.


# 14. Phase G — Reconciliation and close changes for three methods

File:

`ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`

## 14.1 Centralize movement classification — implement before the first value-only adjustment

This is a **Stage 1/2 prerequisite**, not a final clean-up. Current code treats only movement codes beginning with `ADJUST_` as adjustment.

Create a centralized classifier, e.g.:

```text
StockValuationMovementClassifier
```

Classify value-only adjustments including:

```text
ADJUST_IN
ADJUST_OUT
PURCHASE_PRICE_VARIANCE_IN
PURCHASE_PRICE_VARIANCE_OUT
LANDED_COST_IN
LANDED_COST_OUT
STANDARD_REVALUE_IN
STANDARD_REVALUE_OUT
RETURN_STANDARD_VARIANCE_IN
RETURN_STANDARD_VARIANCE_OUT
COST_METHOD_CUTOVER_IN
COST_METHOD_CUTOVER_OUT
```

Financial snapshot must present these as adjustments, not normal receipt/issue quantity.

## 14.2 Generic close invariants

For every method:

```text
OpeningQty
+ InQty
+ AdjustmentQty
- OutQty
= ClosingQty
```

```text
OpeningValue
+ InValue
+ AdjustmentValue
- OutValue
= ClosingValue
```

Block when:

- closing qty < 0;
- closing value < 0;
- qty == 0 and value != 0;
- physical quantity != valued quantity;
- unresolved sales COGS ownership exists;
- required cost evidence unresolved;
- PI adjustment posting is partially applied;
- cost policy missing.

## 14.3 Method-specific close invariants

### WAC

```text
StockCostState
== cumulative valued facts
```

and:

```text
CurrentUnitCost
== InventoryValue / OnHandQty
```

except zero qty => zero cost.

### FIFO

Also require:

```text
open FIFO qty/value
== StockCostState qty/value
```

### STANDARD

Require:

```text
InventoryValue
== OnHandQty * effective standard cost
```

after all standard revaluation postings up to close watermark.

---

# 15. Phase H — SHOULD: persistent stock freeze

Current file:

`ErpWeb.Core/StockLedger/StockPostingGuards.cs`

Current DI:

`IStockFreezeGuard -> NoActiveStockFreezeGuard`

The current guard is a no-op.

## 15.1 Create stock freeze entity

### `StockFreeze`

```text
Id                    bigint PK
CompanyCode
BranchCode
FreezeType            nvarchar(30)
SourceDocumentType    nvarchar(30)
SourceDocumentId      nvarchar(64)

WarehouseCode         nvarchar(20) NULL
LocationCode          nvarchar(10) NULL
ProductionLocationId  bigint NULL

Status                nvarchar(20) -- ACTIVE / RELEASED
StartedAtUtc
StartedBy
ReleasedAtUtc NULL
ReleasedBy NULL
Reason
RowVersion
```

## 15.2 Replace guard

Create:

```text
PersistentStockFreezeGuard : IStockFreezeGuard
```

Register it instead of `NoActiveStockFreezeGuard`.

The guard must:

- use trusted company/branch from posting context;
- reject a posting whose `FreezeScopes` overlaps an ACTIVE freeze.

## 15.3 Populate `FreezeScopes`

Enhance `InventoryStockPostingCommandFactory.CreateAsync()` to derive warehouse/location scopes from the batch's current revision lines.

Sales posting command paths must pass the shipment warehouse/location scopes.

## 15.4 Stock count lifecycle

Use `IvStockCountService`.

Suggested freeze lifecycle:

```text
DRAFT
    -> no freeze

COUNTED
    -> create ACTIVE freeze for the counted warehouse/location scope

POSTED
    -> release after successful stock adjustment + header commit

CANCELLED
    -> release

ROLLED_BACK / Recover
    -> release or recreate according to current count state
```

If the current stock count can span multiple warehouses/locations, create one freeze row per unique scope.

This enhancement is SHOULD, but implement it before enabling high-volume costing reports in production because it materially improves count integrity.

---

# 16. Phase I — SHOULD: Landed Cost

Implement only after PI variance passes all acceptance tests.

Reuse the `PurchaseCostAdjustment` architecture.

## 16.1 Supported cost components

Minimum:

```text
FREIGHT
INSURANCE
IMPORT_DUTY
HANDLING
FORWARDER
OTHER
```

## 16.2 Allocation bases

Support only practical SME bases:

```text
QUANTITY
VALUE
WEIGHT
MANUAL
```

Do not add advanced allocation rules in this phase.

## 16.3 Posting rule

Landed-cost document:

```text
save allocation
    ->
validate total allocated = source charge
    ->
create StockPosting
    ->
append immutable PurchaseCostAdjustment rows
    ->
append value-only StockValuationFact(s)
    ->
update WAC/FIFO state
    ->
seal
```

STANDARD:

- do not change inventory standard value;
- record landed-cost variance separately.

## 16.4 Reversal

Never edit original allocation.

Reverse exact original allocation rows/facts.

---

# 17. Cost method cutover

Changing an active branch method is financially dangerous. A cutover must never create a second opening balance while leaving the prior method balance active.

Create:

```text
IStockCostMethodCutoverService
StockCostMethodCutoverService
```

## 17.1 Allowed cutover

Only when:

- prior accounting period is closed;
- new effective date is first day of an open period;
- no sealed stock posting exists after requested cutover date unless explicitly rolled back;
- no active stock freeze;
- physical/value reconciliation passes;
- no unresolved PI settlement/COGS/cost evidence;
- STANDARD target has approved cost for every positive-stock item;
- user has explicit `COSTING_METHOD_CHANGE` permission.

## 17.2 Mandatory cutover posting shape

Create one atomic `StockPosting`:

```text
CommandType        = COST_METHOD_CUTOVER
SourceModule       = INVENTORY_COSTING
SourceDocumentType = COST_METHOD_POLICY
PostingRole        = PRIMARY
```

For every positive-stock item, append **two linked method-transfer facts** using the same quantity and pre-cutover Inventory value:

```text
OLD method fact
MovementCode = COST_METHOD_CUTOVER_OUT
CostMethod   = old method
Direction    = -1
BaseQty      = on-hand qty
CostAmount   = old authoritative Inventory value

NEW method fact
MovementCode = COST_METHOD_CUTOVER_IN
CostMethod   = new method
Direction    = +1
BaseQty      = same qty
CostAmount   = same value
```

Across the branch/item ledger:

```text
net quantity change = 0
net value change    = 0
```

After the transfer:

```text
old StockCostState qty/value = 0
new StockCostState qty/value = transferred qty/value
```

This prevents method double-counting in `StockValuationSnapshotBuilder`, which groups facts by item + method.

The policy revision becomes active only if the full cutover posting/state initialization commits.

## 17.3 WAC -> FIFO

After `COST_METHOD_CUTOVER_IN` initializes FIFO method state:

Preferred when defensible open receipt history is complete:
- build opening FIFO layers whose total qty/value exactly equals the cutover-in fact.

Fallback:
- one `OPENING_APPROVED` FIFO layer per item for the full cutover qty/value.

Never fabricate detailed historical layers from `IvStockMaster.PurchasePrice`.

Invariant:

```text
sum opening FIFO layer qty/value
= FIFO cutover-in qty/value
```

## 17.4 FIFO -> WAC

After method transfer:

```text
AverageUnitCost / CurrentUnitCost
= transferred InventoryValue / transferred OnHandQty
```

No FIFO layers remain active under the new method; preserve old layers for audit but mark them closed by cutover linkage rather than deleting them.

## 17.5 WAC/FIFO -> STANDARD

Step 1: method-transfer old method OUT -> STANDARD IN at **old Inventory value**.

Step 2: initialize Standard state.

Step 3: append a separate `STANDARD_COST_REVALUATION` value-only posting so:

```text
InventoryValue
=
OnHandQty * effective StandardCost
```

Keeping transfer and revaluation distinct makes the policy change value-neutral and the accounting value change auditable.

## 17.6 STANDARD -> WAC

Perform method transfer at current Standard Inventory value. New WAC opening unit cost:

```text
CurrentUnitCost
=
InventoryValue / OnHandQty
```

No additional revaluation is required merely for the method change.

## 17.7 STANDARD/WAC -> FIFO

Use the same value-neutral method transfer and FIFO opening-layer rule above.

## 17.8 Cutover rollback

Rollback is allowed only if no later sealed posting depends on the new method.

Reverse exact cutover/revaluation facts and method-state initialization; never recalculate current value. Policy revision activation/supersession must be rolled back in the same transaction.

# 18. Posting validation matrix

The AI agent must implement server-side checks. UI checks are convenience only.

| Transaction | WAC | FIFO | STANDARD |
|---|---|---|---|
| GR | approved provisional PO/base cost | create receipt layer | value at effective std cost |
| PI | post inventory/consumed PPV | adjust open layers + consumed variance | full PPV, no inventory revalue |
| Purchase CN | exact reverse/variance logic | exact layer/variance reverse | standard/PPV reverse |
| Misc Receipt | approved cost evidence | approved cost creates layer | standard cost |
| Stock Adj + | WAC unless approved override | new approved layer | standard cost |
| Stock Adj - | current WAC | consume FIFO | standard cost |
| Stock Count + | WAC / approved opening | new count layer | standard cost |
| Stock Count - | current WAC | consume FIFO | standard cost |
| Transfer | value-neutral | no new financial layer | value-neutral |
| DO | WAC COGS | FIFO layer COGS | standard COGS |
| Direct Invoice | WAC COGS | FIFO layer COGS | standard COGS |
| LinkDo Invoice | reuse DO COGS | reuse DO COGS | reuse DO COGS |
| Sales Return | original outbound cost | new return layer at original cost | current std inventory + explicit variance |
| Rollback | exact fact reversal | exact layer consumption reversal | exact fact/variance reversal |

---

# 19. Required source-file change map

This is not exhaustive for configuration/migration files, but the agent must anchor work here.

## Existing files to modify

### Stock ledger

- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- `ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`
- `ErpWeb.Core/StockLedger/StockPostingGuards.cs`
- `ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

### Model

- `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`
- `ErpWeb.Model/Configurations/StockLedger/StockValuationFactConfiguration.cs`
- `ErpWeb.Model/Data/AppDbContext.cs`
- relevant model configuration registration
- `scripts/create-stock-valuation-ledger.sql` or a new idempotent follow-up costing enhancement SQL script

### Inventory

- `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`
- `ErpWeb.Core/Inventory/IvMiscReceiptService.cs`
- `ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`
- `ErpWeb.Core/Inventory/IvStockCountService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`
- `ErpWeb.Model/Entities/Inventory/IvTrxBatchDetail.cs`
- `ErpWeb.Model/Entities/Inventory/IvTrxHistory.cs`

### Procurement

- `ErpWeb.Core/Purchase/PoInvoiceService.cs`
- `ErpWeb.Core/Purchase/PoCdnService.cs`
- `ErpWeb.Model/Entities/Purchase/PoInvoice.cs` (`CostingRevision`)
- `ErpWeb.Model/Entities/Purchase/PoCdn.cs` (`CostingRevision`)
- `ErpWeb.Model/Entities/Purchase/PoInvoiceDetail.cs` only if additional persisted financial reference fields are necessary; do not duplicate data already available in adjustment tables.

### Sales

- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`
- `ErpWeb.Core/Sales/SaCdnService.cs`
- corresponding sales entities only where exact source-line/return lineage cannot be persisted through current inventory batch/history identities.

### Reporting UI

- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor`
- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor.cs`
- `ErpWeb/Inventory/IvStockValueExportEndpoints.cs`

Do not rewrite `IvStockSummaryService` solely to become costing authority. Keep stock summary as a quantity/operational inquiry.

## New files/tables expected

```text
StockCostPolicyRevision
PurchaseReceiptCostSettlement
PurchaseCostAdjustment
SalesReturnCostAllocation
ProductionStandardCostVariance
StockFifoLayer
StockFifoLayerConsumption
ItemStandardCostRevision
StockFreeze
```

Services:

```text
IStockCostMethodResolver
StockCostMethodResolver

ICompanyCurrencyRateResolver
CompanyCurrencyRateResolver

PurchaseCostPostingCommandFactory
IPurchaseCostAdjustmentPostingService
PurchaseCostAdjustmentPostingService
IStockValueAdjustmentWriter
StockValueAdjustmentWriter

IInventoryCostingStrategy
MovingAverageCostingStrategy
FifoCostingStrategy
StandardCostingStrategy
InventoryCostingStrategyResolver

IStockValuationReportService
StockValuationReportService

IPurchaseCostInquiryService
PurchaseCostInquiryService

ISalesGrossProfitInquiryService
SalesGrossProfitInquiryService

IStockCostMethodCutoverService
StockCostMethodCutoverService

PersistentStockFreezeGuard
```

Landed-cost service/page may be added in the SHOULD phase after PPV is stable.

---

# 20. Monetary precision

Current code sometimes uses `IvQty.Round` for values.

Create/centralize financial precision helpers:

```text
RoundQuantity
RoundUnitCost
RoundMoney
RoundRate
```

Recommended current schema-compatible precisions:

```text
Qty       decimal(19,6)
UnitCost  decimal(19,6)
Money     decimal(19,6)
FX Rate   decimal(19,8)
```

Rules:

- carry full configured precision through calculation;
- round only at documented boundaries;
- final depletion takes exact remaining value;
- sum of split cost amounts must equal parent/source cost amount exactly after final-line residual adjustment.

---

# 21. Backdated transaction policy

Preserve current WAC protection.

General rule:

```text
Closed period
    -> reject

Open period + no later valued movement affecting the required costing state
    -> allowed

Open period + later valued movements
    -> reject and require rollback/repost
```

For FIFO:
- a backdated receipt/issue that would change layer order after later FIFO consumption must be rejected unless later affected postings are rolled back first.

For STANDARD:
- historical transaction uses the standard effective on that transaction date;
- a backdated standard revision that would alter sealed postings is rejected.

No silent historical recomputation.

---

# 22. Reversal rules

Every financial feature in this plan must implement rollback before it is considered complete.

## Generic rule

Rollback:

```text
load original posting/facts
    ->
append exact compensating facts
    ->
reverse exact state mutation
    ->
preserve original record
```

Never:

```text
query current cost
recalculate what the old cost "should have been"
```

## PI rollback

Reverse:

- `PurchaseCostAdjustment`;
- inventory value adjustment fact;
- WAC state change / FIFO layer adjustment;
- consumed variance record;
- PO invoiced quantity.

## Standard revaluation rollback

Reverse exact revaluation amount and restore prior current standard state only when no later dependent posting makes rollback invalid.

## FIFO issue rollback

Restore exact original FIFO layers and exact consumed values.

---

# 23. Reconciliation service

Create or extend reconciliation so management reports are not enabled while costing is inconsistent.

Minimum findings:

```text
PHYSICAL_QTY_VS_VALUED_QTY
VALUATION_VS_COST_STATE
ZERO_QTY_RESIDUAL_VALUE
NEGATIVE_VALUE
MISSING_COST_POLICY
MISSING_COST_EVIDENCE
UNVALUED_FACT
PI_SETTLEMENT_QTY_MISMATCH
PI_SETTLEMENT_VALUE_MISMATCH
PI_ADJUSTMENT_MISMATCH
PI_VARIANCE_NOT_BALANCED
COGS_OWNER_UNRESOLVED
SALES_RETURN_ORIGINAL_COST_UNRESOLVED
SALES_RETURN_ALLOCATION_QTY_MISMATCH
SALES_RETURN_ALLOCATION_VALUE_MISMATCH
FIFO_LAYER_QTY_MISMATCH
FIFO_LAYER_VALUE_MISMATCH
FIFO_CONSUMPTION_MISMATCH
STANDARD_COST_MISSING
STANDARD_VALUE_MISMATCH
PRODUCTION_STANDARD_VARIANCE_MISMATCH
COST_METHOD_CUTOVER_MISMATCH
WAREHOUSE_VALUE_ALLOCATION_MISMATCH
ACTIVE_FREEZE_CONFLICT
```

Severity:

- all above are **blocking** for financial period close where relevant.

---

# 24. Database migration / deployment strategy

Do not modify production data destructively.

## 24.1 Additive migrations first

Deploy:

- new tables;
- new nullable evidence/lineage fields;
- `CurrentUnitCost`;
- indexes/constraints;
- new services disabled behind existing WAC policy default.

## 24.2 Seed WAC policy

Create policy revisions for existing active branches.

## 24.3 Backfill only defensible data

Safe backfill:

- `StockCostState.CurrentUnitCost = AverageUnitCost`;
- existing valuation facts already contain `MOVING_AVERAGE`;
- create no fake FIFO layer history unless running an explicit cutover.

## 24.4 Enable new WAC hardening first

Before FIFO/Standard:

- PI FX gate;
- PI variance;
- MR/ADJ/COUNT cost gates;
- sales return lineage;
- strict COGS resolution;
- authoritative valuation report.

Only after WAC path reconciles should FIFO/Standard be enabled.

---

# 25. Implementation sequence for AI Coding Agent

The agent must follow this order. Do not start FIFO/Standard before hardened WAC, exact PI settlement, Sales lineage, and authoritative reports reconcile.

## Stage 1 — Freeze baseline, movement taxonomy and preserve WAC

1. Add cost policy entity/config/resolver.
2. Seed existing branches to `MOVING_AVERAGE`.
3. Add `CurrentUnitCost`.
4. Create centralized financial precision helpers.
5. Create `StockValuationMovementClassifier` **now**, before value-only adjustments exist.
6. Refactor current WAC into `MovingAverageCostingStrategy` with mathematical parity.
7. Verify Production stock-ledger tests and existing Inventory/Sales valuation outputs remain unchanged.

**Exit gate:** existing WAC/Production facts and close results are identical before and after refactor.

## Stage 2 — Procurement exact settlement and financial posting generation

8. Add shared company currency/FX resolver; migrate GR/PI/PoCdn to it.
9. Add `PoInvoice.CostingRevision` and `PoCdn.CostingRevision`.
10. Add `PurchaseReceiptCostSettlement`.
11. Add `PurchaseCostAdjustment`.
12. Add `PurchaseCostPostingCommandFactory` and `PurchaseCostAdjustmentPostingService`.
13. Add `StockValueAdjustmentWriter` for zero-qty value facts/state changes.
14. Implement exact GR -> PI settlement allocation.
15. Implement WAC PI variance using one item-level capitalization budget per PI posting.
16. Implement PI exact rollback + CostingRevision increment.
17. Implement Purchase CN/DN price-only, debit-note and physical-return variance contracts.

**Exit gate:** multiple GR + partial PI + second PI + rollback/repost reconcile exact receipt settlement qty/value with no `StockPosting` uniqueness collision.

## Stage 3 — Manual Inventory cost gates

18. Add structured cost-evidence fields.
19. Enforce MR approved/zero/opening cost evidence.
20. Enforce Stock Adjustment costing by active method.
21. Change Stock Count financial valuation to active method policy.
22. Add manual cost override permission/audit.

**Exit gate:** arbitrary entered price cannot silently change authoritative Inventory value.

## Stage 4 — Sales COGS and return allocation correctness

23. Remove document/item COGS fallback.
24. Add exact owner resolution for DO/LinkDo/direct Invoice.
25. Add `SalesReturnCostAllocation`.
26. Allocate partial returns across all original valuation splits deterministically.
27. Add cumulative over-return guard using active allocation rows.
28. Implement WAC/FIFO/Standard return behavior and exact rollback.
29. Verify no-stock CN remains revenue-only.

**Exit gate:** same-item duplicate lines, mixed DO/direct invoice, multi-split FIFO sale and repeated partial returns reconcile exact COGS once.

## Stage 5 — Authoritative reports and warehouse semantics

30. Add `StockValuationReportService`.
31. Implement branch/item authoritative value plus warehouse allocated drill-down semantics.
32. Rewire `IvStockValue` screen/export.
33. Add Purchase Cost / settlement / PPV inquiry.
34. Add Sales Gross Profit inquiry.
35. Add unresolved-cost indicators and report reconciliation checks.

**Exit gate:** UI, export and snapshot/fact totals agree; warehouse rows reconcile exactly to branch/item value and are labeled correctly.

## Stage 6 — FIFO

36. Add FIFO layer/consumption schema.
37. Implement FIFO receipt/issue with deterministic locking.
38. Implement exact FIFO rollback from consumption rows.
39. Implement PI settlement variance against exact originating layers.
40. Implement FIFO sales return from `SalesReturnCostAllocation`.
41. Add FIFO close reconciliation.

**Exit gate:** known FIFO scenarios and PI/return corrections produce exact expected COGS, open layers and value.

## Stage 7 — Standard Cost + Production bridge

42. Add standard cost revision schema/service.
43. Implement Standard strategy for Inventory/Procurement/Sales.
44. Implement Standard PI PPV.
45. Implement standard-cost revaluation.
46. Add `ProductionStandardCostVariance` and FG actual->standard bridge.
47. Implement Standard sales return + return-standard variance.
48. Add Standard/Production bridge close reconciliation.

**Exit gate:** Inventory value equals qty × effective standard after required revaluation; Production actual FG value = Inventory standard FG value + explicit production variance.

## Stage 8 — Controlled method cutover

49. Implement value-neutral `COST_METHOD_CUTOVER_OUT/IN` posting.
50. Initialize new method state/layers from cutover-in value.
51. For target STANDARD, post separate revaluation after cutover.
52. Prevent casual policy edits and add audit/rollback.

**Exit gate:** old method state is zero, new method state carries exactly one copy of qty/value, and total branch Inventory qty/value is not doubled.

## Stage 9 — SHOULD: persistent stock freeze

53. Add `StockFreeze`.
54. Implement `PersistentStockFreezeGuard`.
55. Populate posting command freeze scopes.
56. Wire stock count lifecycle.

## Stage 10 — SHOULD: landed cost

57. Reuse exact receipt-settlement / purchase-adjustment framework.
58. Add practical charge/allocation bases.
59. Add WAC/FIFO/Standard handling and exact rollback.
60. Include in Purchase Cost inquiry.

## Stage 11 — final reconciliation/deployment gate

61. Add all method/settlement/return/cutover/production-variance findings.
62. Validate snapshot and report totals.
63. Verify no official costing report depends on mutable `PurchasePrice`/`IvBalLoc.UnitPrice`.
64. Run SQL Server concurrency/integration suites for affected posting paths.
65. Run full existing Production stock-ledger regression suite.

# 26. Required test coverage

Costing is a financial subsystem; targeted automated tests are mandatory.

Do not add huge UI test suites. Focus on ledger invariants.

Use existing folders/namespaces:

```text
ErpWeb.Tests/Inventory/Transaction
ErpWeb.Tests/Inventory/Inquiry
ErpWeb.Tests/Procurement/Transaction
ErpWeb.Tests/Sales/Transaction
ErpWeb.Tests/Other
```

## 26.1 WAC

Test:

```text
opening 0
GR 100 @ 10
GR 100 @ 12
issue 150
```

Expected:

```text
avg before issue = 11
COGS = 1650
remaining qty = 50
remaining value = 550
```

Final depletion must remove exact remaining value.

## 26.2 PI variance

Case:

```text
GR 100 @ provisional 10
PI 100 @ actual 11
current on-hand 40
```

Expected by policy:

```text
total variance = 100
inventory portion = 40
consumed variance = 60
```

Then rollback returns exactly to pre-PI values.

Also test negative price variance.

## 26.3 Missing foreign FX

Foreign PI with no valid rate:

```text
SAVE/POST fails
no InvoicedQty changed
no valuation fact
no PurchaseCostAdjustment
```

## 26.4 MR zero cost

Zero-cost MR without `ZERO_COST_APPROVED` fails.

With approval succeeds and leaves audit evidence.

## 26.5 Stock adjustment

Positive adjustment with arbitrary cost but no override permission/evidence fails.

## 26.6 Stock count

Count gain never uses mutable `PurchasePrice` as final cost authority.

## 26.7 DO / Invoice ownership

Test:

- DO shipped -> one COGS owner.
- LinkDo invoice -> zero additional stock-out facts.
- direct invoice -> invoice-owned COGS.
- mixed invoice -> report totals both once.

## 26.8 Duplicate item lines

Invoice with two lines for the same item.

If exact line source identity is missing:

```text
both must not receive whole-document same-item COGS
IsResolved=false
```

## 26.9 Sales return

Test:

```text
DO -> Invoice LinkDo -> CN ReturnStock
```

Return must resolve DO-owned original valuation.

Partial return twice must not exceed original outbound quantity.

## 26.10 FIFO

Receipt:

```text
100 @ 10
100 @ 12
issue 150
```

Expected:

```text
COGS 1600
remaining 50 @ 12 = 600
```

Rollback restores:

```text
100 @ 10
100 @ 12
```

exactly.

## 26.11 Standard

Standard 10:

```text
GR actual 11 x 100
```

Expected:

```text
inventory = 1000
PPV = 100
```

Sale 20:

```text
COGS = 200
inventory = 800
```

Change standard to 12 with 80 on hand:

```text
revaluation +160
inventory = 960
```

## 26.12 Multiple GR / partial PI settlement

Case:

```text
PO line 100
GR1 40 @ base 10.00
GR2 30 @ base 10.20
GR3 30 @ base 10.50
PI1 settles 50
PI2 settles 50
```

Assert:

- PI1 allocates GR1 40 + GR2 10;
- PI2 allocates GR2 remaining 20 + GR3 30;
- every GR fact settled qty <= receipt fact qty;
- total active settled qty = 100;
- provisional settlement amounts tie exactly to receipt facts after final residual handling;
- rollback PI1 frees exactly its 50 qty and values without altering PI2 settlement.

## 26.13 PI rollback / repost revision

```text
PI revision 0 POST
-> PRIMARY StockPosting rev 0
rollback
-> REVERSAL rev 0
-> PI CostingRevision becomes 1
repost
-> PRIMARY rev 1
```

Assert no source/revision uniqueness collision and old facts remain immutable.

## 26.14 Cost method cutover

WAC state:

```text
qty 100
value 1,000
```

WAC -> FIFO cutover expected:

```text
WAC CUTOVER_OUT  qty 100 value 1,000
FIFO CUTOVER_IN  qty 100 value 1,000
net branch qty/value change = 0
old WAC state = 0/0
new FIFO state = 100/1,000
opening FIFO layers total = 100/1,000
```

No double-counted valuation snapshot is permitted.

## 26.15 Production STANDARD FG bridge

```text
Production actual FG value = 1,050
FG qty = 100
standard cost = 10
```

Expected:

```text
Inventory FG receipt = 1,000
ProductionStandardVariance = +50
1,050 = 1,000 + 50
```

Rollback reverses both exact Inventory fact and variance record.

## 26.16 Multi-fact FIFO sales return

```text
sale qty 150
fact A 100 @ 10
fact B  50 @ 12
return 120
```

Expected return allocation:

```text
100 against A = 1,000
20 against B   =   240
return basis   = 1,240
```

A later return may use only the remaining 30 from B.

## 26.17 Purchase CN/DN

Test separately:

- price-only CN: no physical stock movement, negative cost adjustment;
- DN: no physical stock movement, positive cost adjustment;
- CN + ReturnStock: Vendor Return valued by active method plus separate supplier-credit variance;
- rollback restores exact physical and financial state;
- no use of `PoCdnDetail.CostPrice` as Inventory cost.

## 26.18 Warehouse value allocation

For each method, sum all warehouse allocated values for an item and assert exact equality to authoritative branch/item value. Under FIFO, verify the report labels the warehouse value as allocated rather than exact layer ownership.

## 26.19 Close

Close fails for:

- unresolved valuation;
- cost-state mismatch;
- FIFO layer mismatch;
- standard inventory mismatch;
- unresolved COGS;
- zero qty residual value.

---

# 27. SQL concurrency and locking requirements

Costing changes must follow existing repository locking style.

For any posting affecting financial cost:

1. trusted tenant/branch scope;
2. branch transaction lock where existing stock posting path uses it;
3. lock source document;
4. lock cost state / FIFO layers in deterministic key order;
5. mutate physical and financial state in one transaction;
6. append history/facts;
7. seal posting;
8. commit.

FIFO layer lock order:

```text
Company
Branch
Item
ReceiptEffectiveAt
LayerId
```

PI settlement/adjustment lock order must be deterministic by:

```text
PO No
PO Revision
PO Line
Item
Receipt BusinessDate
Receipt PostingSequence
Receipt ValuationFactId
```

Sales-return allocation lock order:

```text
Original owner type
Original owner document
Original owner line
Original StockValuationFactId
```

Cost-method cutover locks the branch and then item states in `ItemCode` order before any OUT/IN facts are appended.

Avoid deadlock-prone inconsistent ordering.

---

# 28. Security and audit

Add/extend permissions for:

```text
COSTING_SETUP
COSTING_METHOD_CHANGE
STANDARD_COST_APPROVE
MANUAL_COST_OVERRIDE
VIEW_COST
EXPORT_COST
```

Reuse the repository's existing menu/permission infrastructure.

Audit at minimum:

- costing-method policy revision;
- method cutover;
- standard cost revision;
- standard revaluation;
- manual/zero/opening cost approval;
- PI variance;
- landed cost;
- stock freeze release/override.

Do not expose cost values to users without the equivalent `VIEW_PRICE` / costing permission.

---

# 29. Agent guardrails

The coding agent MUST NOT:

1. create another financial ledger table that competes with `StockValuationFact`;
2. create another stock-posting coordinator;
3. use `IvStockMaster.PurchasePrice` as historical cost fallback;
4. use `IvBalLoc.UnitPrice` as historical report authority;
5. use physical shipment FIFO as financial FIFO;
6. duplicate COGS for `LinkDo` invoice lines;
7. recompute rollback using current cost;
8. silently default foreign FX to `1`;
9. silently accept zero-cost stock without explicit evidence;
10. modify sealed valuation facts;
11. retroactively recalculate sealed WAC/FIFO/Standard issues;
12. permit cost-method change by a simple dropdown update while stock exists;
13. build official management profit totals from unresolved COGS;
14. remove existing no-future-stock or period-close checks;
15. break current Production stock-ledger integration.
16. calculate PI variance from PO price when exact GR valuation settlement evidence exists.
17. reuse the same current on-hand quantity independently for several PI lines of the same item when splitting WAC variance.
18. repost a rolled-back PI/CN with the same `DocumentRevision`/costing generation.
19. create a new method opening balance without zeroing/transferring the old method state.
20. collapse a FIFO sales return that references multiple outbound valuation facts into one guessed `OriginalValuationFactId`.
21. treat Production actual-to-Standard variance as Inventory value.
22. claim warehouse FIFO value is exact layer ownership while financial FIFO is branch/item pooled.
23. use `PoCdnDetail.CostPrice` as Vendor Return Inventory cost.

If the agent finds an existing implementation already satisfying a step, it should verify and reuse it rather than duplicate it.

---

# 30. Definition of done — 10/10 approval gate

The costing enhancement is **not approved** until all MUST gates below pass.

## Architecture

- one explicit active cost policy per company/branch/business date;
- WAC/FIFO/STANDARD all create authoritative `StockValuationFact`;
- reports consume the same ledger;
- `StockPostingCoordinator` remains the sole posting envelope.

## WAC

- current behaviour remains mathematically correct;
- PI variance changes current financial state under documented policy;
- arbitrary adjustment/count/manual costs cannot corrupt WAC.

## Procurement

- GR provisional cost is traceable.
- missing foreign PI FX blocks.
- PI creates exact GR-level settlement rows and auditable variance.
- multiple GR / partial PI allocations are deterministic and never over-settle a receipt fact.
- PI rollback/repost advances `CostingRevision` and does not collide with `StockPosting` uniqueness.
- inventory vs consumed variance balances to total PI variance.
- rollback exactly reverses it.
- purchase CN/DN distinguishes price-only adjustment from optional Vendor Return and records any physical-return vs supplier-credit variance explicitly.

## Sales

- every physical stock-out has exactly one COGS owner.
- LinkDo invoice never duplicates DO COGS.
- direct invoice owns its COGS.
- duplicate same-item invoice lines cannot duplicate COGS through fallback.
- sales return resolves exact original cost lineage through `SalesReturnCostAllocation`.
- one return line may allocate across multiple original valuation facts.
- partial returns cannot over-return any original valuation fact or original sale line.

## FIFO

- layers are financial, not `IvBalLoc`.
- issue consumes oldest financial layer.
- final layer depletion uses exact residual value.
- rollback restores exact original layers.
- PI variance adjusts open layer value and classifies consumed variance.

## Standard

- every stock-controlled STANDARD item has an effective approved standard cost.
- receipt/issue use standard cost.
- PI actual-vs-standard produces PPV.
- standard revision creates explicit revaluation.
- old facts remain immutable.
- Production actual FG value reconciles to Standard Inventory FG value plus explicit `ProductionStandardCostVariance`.

## Reports

- Inventory Valuation no longer uses the old estimate formula as authority.
- Inventory Valuation ties to valuation snapshots/facts.
- Purchase Cost/PPV ties to GR/PI adjustments.
- Gross Profit ties to exact sales COGS facts.
- warehouse valuation drill-down reconciles to authoritative branch/item value and is explicitly labeled as allocation where the cost pool is not warehouse-specific.
- unresolved rows are visible and cannot masquerade as final totals.

## Close

- physical qty = valued qty.
- valuation value = method state.
- FIFO open layers reconcile where FIFO active.
- standard quantity × standard cost reconciles where STANDARD active.
- method cutover leaves exactly one active copy of Inventory qty/value: old method state zero, new method state initialized.
- Production Standard variance reconciliation passes where STANDARD + Production are active.
- zero qty residual value is blocked.
- unresolved COGS/cost evidence blocks close when financially relevant.

## Reversal

Every new financial posting path has an exact reversal path.

---

# 31. Final implementation target

```text
                           COST POLICY
                 MOVING_AVERAGE / FIFO / STANDARD
                              |
                              v
SOURCE DOCUMENTS ------> StockPostingCoordinator
Inventory                    |
Procurement                   v
Sales                  IvTrxHistory
                              |
                              v
                   InventoryValuationService
                              |
                 +------------+-------------+
                 |            |             |
                 v            v             v
              WAC         FIFO layers    STANDARD
                 |            |             |
                 +------------+-------------+
                              |
                              v
                    StockValuationFact
                      (financial truth)
                              |
                 +------------+-------------+
                 |                          |
                 v                          v
             Current state           Period snapshots
                 |                          |
                 +-------------+------------+
                               |
                               v
                    AUTHORITATIVE REPORTS
                 Inventory Valuation
                 Purchase Cost / PPV
                 Sales COGS / Gross Profit
```

---

# 32. Recommended implementation checkpoints

The AI coding agent should produce a checkpoint after each stage containing:

```text
Changed files
New files
DB changes
Posting paths affected
Rollback paths affected
Reconciliation changes
Tests added
Known unresolved items
```

Do not implement FIFO and Standard before the hardened WAC/PI/Sales/reporting stages reconcile.

The safest delivery order is:

```text
WAC hardening
    ->
Procurement actual cost
    ->
Manual inventory cost controls
    ->
Sales COGS/return lineage
    ->
Authoritative reports
    ->
FIFO
    ->
Standard Cost
    ->
Method cutover
    ->
Stock freeze
    ->
Landed cost
```

This order protects the currently working repository while adding the requested costing capabilities on top of the existing V2 stock-ledger foundation.

---

# 33. Final status

**Status: APPROVED — 10/10 IMPLEMENTATION BLUEPRINT**

Repository baseline reviewed:

`mokth/net10projectTemplate` / `production` / `a58c508d8efc3de0bd8b34d7c7c9a3f0d3deecdd`

This revision incorporates the final repo-backed implementation corrections that were missing from the prior draft:

1. exact GR-level Purchase Invoice settlement;
2. PI/Purchase CN financial `CostingRevision` and repost-safe `StockPosting` generation;
3. a concrete procurement value-adjustment posting seam using the existing coordinator/ledger;
4. non-duplicating WAC same-item PI variance allocation;
5. explicit Purchase CN/DN + Vendor Return costing separation;
6. multi-fact Sales Return cost allocation for FIFO and partial returns;
7. branch/item authoritative versus warehouse allocated valuation semantics;
8. Production actual FG -> Standard Inventory bridge with explicit variance;
9. value-neutral old-method OUT -> new-method IN cost-method cutover;
10. movement classification moved ahead of all value-only adjustment features;
11. added settlement/return/cutover/Production reconciliation and acceptance tests.

The AI Coding Agent should implement the plan stage-by-stage and must satisfy each stage exit gate before proceeding. It must preserve the current V2 `StockPosting` / `StockValuationFact` architecture, existing Production actual-cost evidence, exact reversals, no-future-stock protection, backdated-value protection, and period-close invariants.

If the branch changes after this baseline commit, the agent may map file names/classes to the new source, but it must not weaken or reinterpret the financial contracts in this plan.
