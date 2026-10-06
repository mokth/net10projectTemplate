# Costing Diagnostic & Repair Center --- Production-Branch Implementation Plan

**Repository:** `mokth/net10projectTemplate`\
**Branch reviewed:** `production`\
**Reviewed HEAD:** `5b7478b4ce26927de325c3c7a42648739754dac2`
(`enhance production ui`)\
**Plan status:** **FIFTH-REVIEW OWNERSHIP CORRECTIONS INCORPORATED and checked against HEAD `5b7478b4`.** Repair adapters are chosen by resolved business owner, not by raw `StockPosting.SourceDocumentType`. Implement Phase 1, Phase 2, and Phase 3 only in that order, and only as sections 2A.4, 4A, 8A, 9.3, 11, and 12 now read.\
**Primary objective:** Give normal ERP users a safe, explainable way to
detect, trace, understand, and repair costing problems without SQL
access or programmer debugging.

------------------------------------------------------------------------

## 1. Executive decision

Implement a new **Costing Diagnostic & Repair Center** under Inventory.

This is a **Must Enhance / Critical** feature.

The current project already has substantial quantity, chronology,
posting, valuation, period-close, production-pool, reversal, and
reconciliation protections. The missing piece is an operator-facing
layer that answers:

1.  **Is costing healthy?**
2.  **Which item/document caused the problem?**
3.  **Why is the cost what it is?**
4.  **What downstream documents are affected?**
5.  **Can the problem be repaired safely?**
6.  **What exactly will the repair change?**
7.  **Did the repair restore all invariants?**

The page must **not** become a cost editor. It must never directly
overwrite `StockValuationFact`, `StockCostState`, posted `IvTrxHistory`,
production valuation rows, or posted document cost fields.

Repairs must use controlled domain operations, reversal/repost, or a
future purpose-built authoritative recalculation service.

------------------------------------------------------------------------

# 2. Repo findings that drive this design

## 2.1 V2 stock ledger is the monetary source of truth

Relevant existing files:

-   `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`
-   `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
-   `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
-   `ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`
-   `ErpWeb.Core/StockLedger/StockPostingGuards.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockPosting.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockValuationPeriodSnapshot.cs`

`StockPostingCoordinator` already owns the V2 posting envelope, branch
sequencing, period guard, idempotency/replay protection, valuation
execution, and sealing.

`StockValuationFact` already contains the evidence needed for an
explainable trace:

-   source document type/id/no/line
-   effective/business date
-   item/warehouse/location/lot
-   movement code
-   direction and base quantity
-   cost method
-   unit cost and cost amount
-   transaction currency/amount/rate
-   valuation source/status/version
-   inventory history link
-   production movement/work-order links
-   original valuation fact
-   reversed valuation fact

This should be the primary monetary trace displayed by the new center.

------------------------------------------------------------------------

## 2.2 Current authoritative V2 method is Moving Average

`StockValuationFact.CostMethod` defaults to:

`StockCostMethods.MovingAverage`

and the current `StockCostMethods` contains:

`MOVING_AVERAGE`

`InventoryValuationService` maintains `StockCostState` using:

-   `OnHandBaseQty`
-   `InventoryValue`
-   `AverageUnitCost`
-   `LastValuationFactId`
-   `LastPostingSequence`

The unique pool is `CompanyCode + BranchCode + ItemCode + CostMethod`
(`UQ_StockCostState_Pool`). It is not keyed by warehouse, location, or
lot. The authoritative Moving Average is therefore **branch + item +
cost method**. Warehouse, location, and lot are movement-evidence
dimensions only.

Consequences:

-   Cost-state quantity, value, and average checks reconcile at
    branch + item + cost method.
-   Do not display or calculate a "warehouse average cost".
-   A warehouse, location, or lot filter must not recompute
    `RunningAvg` from the filtered fact subset. Compute the
    branch-item running state first, then filter what is displayed.
-   A transfer inside the same branch conserves value inside that same
    branch-item pool.

Therefore Phase 1 of the diagnostic center must accurately diagnose the
**current authoritative V2 Moving Average implementation**.

Do **not** label current V2 valuation as FIFO or Standard Cost unless
those methods are subsequently implemented in the authoritative
stock-ledger engine.

The diagnostic architecture must nevertheless be method-aware so future
FIFO and Standard Cost engines can plug into the same UI.

------------------------------------------------------------------------

## 2.3 Current valuation already rejects dangerous states

`InventoryValuationService` already rejects or protects important
conditions, including:

-   backdated valued movements when later valuation facts already exist
-   issue without an approved moving-average opening/receipt value
-   issue quantity exceeding cost-state quantity
-   negative receipt valuation
-   reversal without original valuation facts
-   duplicate valuation reversal
-   reversal that current cost state cannot support

The diagnostic center must translate these technical conditions into
business-readable findings and recommended actions.

------------------------------------------------------------------------

## 2.4 Exact reversal lineage already exists

The V2 design is append-only/reversal-oriented.

Relevant links:

-   `StockPosting.ReversesPostingId`
-   `IvTrxHistory.ReversesHistoryId`
-   `StockValuationFact.ReversesValuationFactId`
-   `StockValuationFact.OriginalValuationFactId`

The repair center must preserve this model.

**Forbidden design:** update old valuation facts in place.

------------------------------------------------------------------------

## 2.5 Quantity reconciliation already exists

Existing:

-   `IIvInventoryReconciliationService`
-   `IvInventoryReconciliationService`
-   UI: `ErpWeb.UI/Inventory/Inquiry/IvReconciliation.razor`

The current page already compares live `IvBalLoc` quantity against
posted movement history and reports findings.

Do not duplicate this logic.

The costing center should call/reuse it and surface relevant
quantity-integrity findings because monetary costing cannot be trusted
when quantity lineage is broken.

------------------------------------------------------------------------

## 2.6 Chronology controls already exist

Relevant:

-   `IvInventoryPostingService.Chronology.cs`
-   `IvStockDateRules`
-   `IvStockMovementRules`

Current rules cover:

-   future stock relative to document date
-   later-day movements
-   rollback chronology
-   balance `TransDate` repair after rollback

The costing center must expose chronology failures as first-class
costing findings because Moving Average is order-sensitive.

------------------------------------------------------------------------

## 2.7 Period close already has integrity gates

`IvPeriodCloseService` already:

-   blocks on unposted batches
-   runs inventory reconciliation
-   rejects blocking reconciliation findings
-   detects mutation after prior close
-   writes quantity snapshots
-   invokes `StockValuationSnapshotBuilder`

The new cost health service should become an additional monetary
pre-close gate after it is proven stable.

Do not initially make every warning block close. Only explicitly
classified **Critical/Blocking** findings should eventually block close.

------------------------------------------------------------------------

## 2.8 Production has separate value lineage that must be included

Relevant files:

-   `ProductionCostReadiness.cs`
-   `ProductionPoolValuationService.cs`
-   `ProductionOutputService.Posting.cs`
-   `ProductionMaterialIssueService.Posting.cs`
-   `ProductionFinishedGoodReceiptService.Posting.cs`
-   `ProductionPostingInvariant.cs`
-   `ProductionBalLot`
-   `ProductionBalLotMovement`
-   `ProductionMovementAllocation`
-   Entity `ProductionPoolValuation` (`DbSet`: `ProductionPoolValuationRows`)
-   Entity `ProductionValuationEvidence` (`DbSet`: `ProductionValuationEvidenceRows`)
-   Entity `ProductionFinishedGoodFact` (`DbSet`: `ProductionFinishedGoodFactRows`)

Production already checks verified inventory cost evidence and pool
quantity/value consistency.

Daily Production consumes pool value and carries actual consumed cost
into produced WIP.

Finished Good Receipt transfers exact production value into inventory
using `ExactTransferredValue` and `FG_EXACT_BASE_CURRENCY` evidence.

Therefore the diagnostic center must trace:

**Inventory RM → Issue to Production → Production Pool/WIP → Daily
Production → FG Receipt → Inventory FG → Sales/COGS**

This is one of the highest-value capabilities of the new feature.

------------------------------------------------------------------------

# 2A. Production, epoch, and snapshot corrections

The following are mandatory refinements from the final source review.

## 2A.1 Production immutable valuation evidence is a first-class source

Production diagnostics must not stop at `ProductionBalLot.TotalCost` and
`ProductionPoolValuation`. Use the entity names
`ProductionPoolValuation`, `ProductionValuationEvidence`, and
`ProductionFinishedGoodFact`. Use the `*Rows` names only for
`AppDbContext` `DbSet` properties.

`ProductionPostingInvariant` proves each V2 production movement against
`ProductionValuationEvidence` rows (`DbSet` `ProductionValuationEvidenceRows`), including:

-   V2 ledger version
-   active ledger epoch
-   owning `StockPosting`
-   VERIFIED valuation status
-   valuation evidence row
-   evidence generation
-   evidence basis
-   company-base currency
-   price
-   conversion factor
-   inventory-history identity for ISSUE movements

The Costing Center must expose this evidence in Production Cost Trace
and must diagnose missing/mismatched evidence.

Add mandatory checks:

### CD-025 --- Production valuation evidence missing

A sealed active V2 Production movement has no matching
`ProductionValuationEvidence` row.

Severity: **Critical**

### CD-026 --- Production valuation evidence mismatch

Evidence exists but disagrees with the movement/pool/posting identity,
generation, currency, price, conversion factor, or required
inventory-history identity.

Severity: **Critical**

Implementation should reuse/extract the same validation semantics used
by `ProductionPostingInvariant`; do not maintain a looser duplicate rule
set.

------------------------------------------------------------------------

## 2A.2 Ledger epoch/cutover must be explicit

`ProductionStockHistoryService` already treats history before the active
`StockLedgerEpoch.EffectiveFrom` as legacy coverage.

The Costing Center must therefore return a coverage state:

-   `V2`
-   `HISTORY_BEFORE_CUTOVER`
-   `NO_ACTIVE_EPOCH`

It must never present pre-cutover history as if V2 monetary lineage were
complete.

Add:

### CD-027 --- V2 coverage unavailable/incomplete

Use `StockLedgerErrorCodes.HistoryBeforeCutover` / epoch state where
applicable.

Severity: **Warning** for historical inquiry coverage; **Critical** when
an attempted repair requires evidence outside authoritative V2 coverage.

------------------------------------------------------------------------

## 2A.3 Closed-period hash and watermark are hard repair boundaries

`StockValuationSnapshotBuilder` stores:

-   `PostingSequenceWatermark`
-   `SourceDataHash`
-   immutable period snapshot lines
-   revision
-   ledger epoch

Repair planning must load the latest applicable valuation snapshot and
treat its watermark/hash as immutable accounting evidence.

A repair is **not executable** if it would alter economic history at or
before a closed snapshot watermark unless the existing
period-close/reopen policy has first legitimately reopened/withdrawn the
relevant close and all later-close dependencies permit it.

Add:

### CD-028 --- Closed valuation snapshot evidence changed

Recompute the canonical source evidence represented by the stored
snapshot and compare to `SourceDataHash` using the same
canonicalization/hash logic.

Severity: **Critical**

Never "repair" this by replacing the stored hash.

------------------------------------------------------------------------

## 2A.4 Repair execution requires document-specific adapters

There is no single generic safe rollback API across Inventory, Sales,
Procurement, and Production.

Do not route from raw `StockPosting.SourceDocumentType`.
`InventoryStockPostingCommandFactory` sets that field to
`batch.TrxType`. Sales, purchase credit notes, and goods receipt then
delegate physical posting to inventory batches, so one physical type
can have more than one authoritative repair service.

Resolve the business owner first:

``` csharp
public interface ICostingRepairOwnershipResolver
{
    Task<CostingRepairOwner> ResolveAsync(
        CostingRepairNode node,
        CancellationToken cancellationToken);
}

public sealed record CostingRepairOwner(
    string OwnerType,
    string OwnerDocumentNo,
    string PhysicalSourceDocumentType,
    string PhysicalSourceDocumentId,
    long StockPostingId);
```

Use stored evidence. Do not infer from dates or items. If ownership
cannot be proven, `CanRepair = false` and the plan says
**Manual/module workflow required**. Never guess.

Resolution rules:

-   Inventory sales-out: use the same rule as
    `InventoryValuationService.ResolveSourceIdentity`. `DoNo` present
    → `SA_DO`. Otherwise `InvNo` present → `SA_INVOICE`. Do not invent
    a second interpretation.
-   Customer return: if `SaCdnCrLock` proves a same-company,
    same-branch, stock-returning sales CN (`CN/<docNo>` is supporting
    evidence only), owner is `SA_CDN`. Otherwise
    `INV_CUSTOMER_RETURN`.
-   Vendor return: if `PoCdn.VrBatchNo` proves a same-company,
    same-branch purchase CN (`PCN/<docNo>` is supporting evidence),
    owner is `PO_CDN`. Otherwise `INV_VENDOR_RETURN`.
-   Goods receipt and non-stock goods receipt: owner is the
    purchase-aware goods-receipt path so PO receive quantities stay
    inside rollback. The generic inventory adapter must not claim them.
-   Issue to production, daily production, and finished-good receipt:
    stay on the production service when production posting links prove
    that ownership. Do not route them through generic inventory repair
    only because inventory rows exist.
-   Direct miscellaneous receipt, miscellaneous issue, scrap, transfer,
    and adjustment stay on the inventory adapter.

Then key adapters by that owner:

``` csharp
public interface ICostingRepairAdapter
{
    IReadOnlySet<string> OwnerTypes { get; }

    Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node,
        CostingRepairOwner owner,
        CancellationToken cancellationToken);

    Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node,
        CostingRepairOwner owner,
        CostingRepairExecutionContext context,
        CancellationToken cancellationToken);

    Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node,
        CostingRepairOwner owner,
        CostingRepairExecutionContext context,
        CancellationToken cancellationToken);
}
```

Routing rule: the resolved `OwnerType` matches exactly one registered
adapter, or zero adapters and the chain stays preview-only. Never more
than one. Raw `StockPosting.SourceDocumentType` is the physical source,
not the registry key.

Register only adapters backed by existing authoritative module services.

If a resolved owner has no proven safe adapter:

-   diagnostics remain available
-   impact preview remains available
-   repair plan says **Manual/module workflow required**
-   `CanRepair = false`

**Forbidden:** generic deletion/update/reinsert of posting/history/fact
rows.

------------------------------------------------------------------------

## 2A.5 Repair graph ordering is posting/dependency based, not date-only

The dependency planner must build nodes using immutable identities and
explicit links:

-   `StockPosting.Id`
-   `PostingSequence`
-   `ReversesPostingId`
-   `StockValuationFact` original/reversal links
-   `IvTrxHistory` reversal links
-   production movement dependencies
-   production movement allocations
-   production posting links
-   FG facts
-   document-specific upstream/downstream links where authoritative

Rollback order:

**dependents → root**

Repost order:

**root → dependents**

`EffectiveAt` is evidence/filtering information; `PostingSequence` and
explicit dependency edges determine safe ledger order.

------------------------------------------------------------------------

## 2A.6 Cost-state reconstruction follows the resolved epoch model

Do not hard-code active-epoch-only reconstruction here.
`StockCostState` verification and rebuild follow the model proven in
section 8A.

Until that gate is resolved:

-   no cross-epoch rebuild is executable
-   diagnostics surface coverage state (`V2`, `HISTORY_BEFORE_CUTOVER`,
    or `NO_ACTIVE_EPOCH`)
-   automated repair that depends on cross-epoch valuation is blocked

Any rebuild still scopes to trusted company, trusted branch, the
supported cost method, sealed facts, and authoritative reversal
semantics.

-   **Model B:** scope facts to the active `LedgerEpochId`.
-   **Model A:** use the tested cumulative rule in diagnostics, COGS,
    rebuild, snapshots, and repair planning.

Do not mix an untested legacy rule into either model. Pre-cutover
history stays visibly distinguished from V2 coverage.

------------------------------------------------------------------------

## 2A.7 Production value conservation must be end-to-end

For each production chain, the diagnostic trace should prove where
applicable:

``` text
verified RM/WIP input value
+ explicitly supported production-added cost
= produced WIP value
= downstream consumed WIP value
= FG exact transferred value
= FG inventory receipt valuation
```

The current implementation primarily carries consumed material/WIP
value. Do not invent labour/machine/overhead monetary cost if the
current posting engine does not authoritatively capitalize those
components.

Show those components as **Not capitalized / Not available in current
valuation engine** rather than displaying an estimated production cost
as authoritative.

# 2B. Inventory + Procurement final approval review

This section is mandatory and is based on a dedicated second source pass
over the Inventory and Purchase modules.

## 2B.1 Inventory transaction coverage is broader than a GRN-only costing trace

`IvInventoryPostingService` is the central stock posting/rollback
dispatcher for:

-   Miscellaneous Receipt
-   Customer Return
-   Goods Receive
-   Non-Stock Goods Receive
-   Miscellaneous Issue
-   Scrap
-   Vendor Return
-   Stock Transfer
-   Stock Adjustment

These paths use branch stock locking and, where V2 is active, the
`StockPostingCoordinator` begin/complete envelope.

The Costing Center must therefore classify every active V2 inventory
fact by these transaction families. It must not implement a special
GRN-only diagnostic path.

Add an **Inventory Transaction Coverage** panel showing, for the
selected period:

-   posted document count by transaction type
-   valued fact count
-   unvalued/missing fact count
-   reversal count
-   unresolved lineage count
-   total qty in/out
-   total value in/out

------------------------------------------------------------------------

## 2B.2 GRN is currently the inventory-cost recognition point

`IvGoodsReceiptService.ResolveReceiptCostEvidenceAsync` freezes receipt
cost from the Purchase Order at GRN creation/posting time.

Current evidence includes:

-   PO / revision / line
-   purchase currency
-   approved exchange rate covering the GRN transaction date
-   net purchase unit cost
-   base-stock unit cost
-   `PriceEvidence = PO_PROVISIONAL|...`

The calculation is effectively:

``` text
NetPurchaseUnitCost = PO line NetAmount / PO purchase qty

BaseStockUnitCost =
    NetPurchaseUnitCost
    × received purchase qty
    × GRN-date exchange rate
    / received base qty
```

The GRN detail freezes:

-   `UnitPrice = PoUnitPrice`
-   `CostPrice = NetPurchaseUnitCost`
-   `BaseUnitPrices = BaseStockUnitCost`
-   `Currency`
-   `PriceEvidence`

This is strong evidence and must be shown in the Cost Explanation UI.

------------------------------------------------------------------------

## 2B.3 Supplier Purchase Invoice does not currently revalue stock

`PoInvoiceService` validates PO/invoice matching and updates PO
`InvoicedQty`. The reviewed code does not post a stock valuation
adjustment when the final supplier invoice unit price differs from the
provisional PO/GRN cost.

Therefore the Costing Center must **not** claim that Purchase Invoice is
the current inventory valuation source.

This creates a required diagnostic/business-policy gap.

### CD-029 --- GRN provisional cost differs from final supplier invoice

For a posted stock GRN that is subsequently matched to a posted Purchase
Invoice, compare the GRN frozen provisional base cost with the final
invoice economic unit cost where an authoritative line relationship can
be proven.

Display:

-   PO price
-   GRN provisional price
-   GRN exchange rate
-   GRN base cost
-   supplier invoice price
-   invoice currency
-   invoice discount/tax treatment relevant to net item cost
-   difference per base unit
-   remaining stock quantity if determinable
-   quantity already consumed/sold/produced if determinable

Severity:

-   **Info** when equal within configured tolerance
-   **Warning** when different and no final-cost revaluation policy
    exists
-   **Critical** only if company policy/configuration explicitly
    requires invoice-final inventory valuation and the variance remains
    unresolved

Do not auto-revalue in Phase 1.

------------------------------------------------------------------------

## 2B.4 Procurement final-cost policy must be explicit before repair automation

Add a configuration/policy concept, but do not silently default to a
destructive behavior:

``` text
PurchaseReceiptCostPolicy
- PO_PROVISIONAL_ONLY
- INVOICE_FINAL_WITH_VARIANCE   (future authoritative implementation)
```

For the current repo, the safe behavior is to report the observed
implementation as `PO_PROVISIONAL_ONLY` unless a verified existing
configuration says otherwise.

A future `INVOICE_FINAL_WITH_VARIANCE` implementation must define
accounting treatment for:

1.  stock still on hand
2.  stock already sold
3.  stock transferred
4.  stock consumed into Production/WIP
5.  WIP already converted to FG
6.  FG already sold
7.  prior closed periods
8.  foreign-exchange difference
9.  purchase-price variance versus inventory revaluation
10. tax/discount/rounding differences

This is a separate costing-engine enhancement. The Diagnostic Center
must expose the gap but must not invent the accounting policy.

------------------------------------------------------------------------

## 2B.5 GRN exchange-rate evidence is a critical cost dependency

GRN posting refuses to freeze receipt cost when no approved currency
rate covers the transaction date.

Add:

### CD-030 --- GRN currency-rate evidence invalid

Detect:

-   missing frozen transaction currency
-   missing/invalid exchange-rate evidence
-   non-positive rate
-   evidence inconsistent with frozen base cost
-   changed source master rate must **not** retroactively invalidate a
    correctly frozen historical receipt

Severity: **Critical** only when the frozen posting evidence itself is
incomplete or mathematically inconsistent.

The diagnostic must not recalculate historical GRNs using today's
exchange rate.

------------------------------------------------------------------------

## 2B.6 PO revision/line identity is part of cost lineage

GRN validates the latest PO revision and stores:

-   `PoNo`
-   `PoRelNo`
-   `PoLineNo`

Cost Trace must display the exact revision/line that supplied the
provisional cost.

Add:

### CD-031 --- Purchase source lineage broken

A GRN valuation fact/history claims PO-derived cost but the frozen GRN
evidence lacks the required PO/revision/line identity or cannot be
reconciled to the immutable document evidence available for that
posting.

Do not flag a later PO revision merely because the live PO has changed.
Historical cost must use frozen posting evidence first.

Severity: **Error/Critical** depending on whether the valuation can
still be proven from immutable GRN/history/fact evidence.

------------------------------------------------------------------------

## 2B.7 Transfer must conserve value

`InventoryValuationService` creates separate `TRANSFER_OUT` and
`TRANSFER_IN` valuation facts.

The diagnostic center must prove for each V2 transfer:

``` text
out base qty == in base qty
out value    == in value
```

subject only to the engine's defined rounding behavior.

Add:

### CD-032 --- Stock transfer value conservation failure

Severity: **Critical**

The destination must not acquire a user-entered or current-master cost
that differs from the authoritative value removed from the source.

If the current engine ever produces such a difference, report it; do not
normalize it in the UI.

------------------------------------------------------------------------

## 2B.8 Stock Adjustment needs explicit valuation provenance

`IvStockAdjustmentService` permits a supplied unit price and, when zero,
can derive from balance `UnitPrice` or item `PurchasePrice`.

This is legitimate operational behavior but weaker valuation evidence
than a GRN.

Add:

### CD-033 --- Adjustment receipt cost provenance weak

For positive adjustments, classify the cost source:

-   explicit approved adjustment price
-   verified balance price
-   item-master purchase-price fallback
-   missing/zero/unverifiable

Show the provenance prominently.

Severity:

-   explicit/verified → Info
-   master fallback → Warning
-   zero/missing/unverifiable for a financially relevant inbound
    adjustment → Critical

A future enhancement should require an adjustment reason and explicit
approved valuation basis for material-value adjustments.

------------------------------------------------------------------------

## 2B.9 Stock Transfer UI fallback price is not authoritative valuation truth

`IvStockTransferService` may display/default a unit price from:

1.  user/input value
2.  balance unit price
3.  item purchase price

The Costing Center must not treat that UI/default price as authoritative
transfer cost.

Authoritative transfer cost is the sealed valuation fact generated by
the valuation engine.

This distinction must be documented in the trace UI.

------------------------------------------------------------------------

## 2B.10 Vendor Return must use outbound valuation, not typed supplier price

Vendor Return documents can contain a unit price for business/reference
purposes. However V2 outbound valuation is produced from the current
authoritative cost state.

Cost Trace must separately display:

-   supplier/document unit price
-   inventory valuation unit cost
-   inventory value removed

Add:

### CD-034 --- Vendor return commercial/valuation price difference

Severity: **Info/Warning**

This is not automatically an error. The user must be able to understand
why the supplier credit amount and inventory value removed differ.

For Purchase CN with `ReturnStock`, `PoCdnService` delegates stock
movement to the Vendor Return posting path and explicitly avoids writing
Cost/CostPrice as inventory valuation truth. The diagnostic center must
preserve this separation.

------------------------------------------------------------------------

## 2B.11 Purchase CN/DN stock and financial effects must be separated

`PoCdnService` supports financial credit/debit behavior and optional
stock return.

The Costing Center must distinguish:

``` text
Financial CN/DN only
    → no physical stock movement

CN with ReturnStock
    → Vendor Return stock movement + financial document

DN
    → financial effect unless an explicit authoritative stock movement exists
```

Add:

### CD-035 --- Procurement financial document incorrectly assumed to move stock

This is primarily a diagnostic/modeling guard for the new UI and future
reporting.

Never infer inventory movement merely because a Purchase CN/DN exists.

------------------------------------------------------------------------

## 2B.12 PO received/returned/invoiced quantities are costing-support evidence

The Purchase Order line tracks:

-   `RecvQty`
-   `ReturnQty`
-   `ReturnQtyCn`
-   `BalanceQty`
-   `InvoicedQty`
-   `OverRecvQty`

The Costing Center should include a procurement matching panel:

``` text
Ordered
Received
Returned
Net received
Invoiced
Uninvoiced received
Invoice variance
```

Add:

### CD-036 --- Procurement quantity matching inconsistency

Detect impossible relationships according to the existing `PoOrderCalc`
/ `PoInvoiceService` rules.

This check supports costing diagnosis but must reuse Purchase
calculation semantics; do not duplicate formulas loosely.

------------------------------------------------------------------------

## 2B.13 Customer Return cost source is special and must remain traceable

Although this is Sales-facing, it is an Inventory receipt path.

`InventoryValuationService` attempts to resolve the original sale cost
and uses `OriginalSaleReturn`; only when that lineage cannot be resolved
does normal receipt cost resolution apply.

Add:

### CD-037 --- Customer return original COGS lineage unresolved

Severity: **Error/Critical** when a posted return cannot prove the cost
restored to inventory.

The UI should link original sale valuation fact ↔ return valuation fact.

------------------------------------------------------------------------

## 2B.14 Miscellaneous Receipt cost must be explainable

Miscellaneous Receipt is valued as an inbound receipt and can use
explicit company base price evidence.

Add:

### CD-038 --- Miscellaneous receipt valuation basis weak/missing

Show the reason code and valuation basis.

A non-zero material receipt without a defensible valuation basis must be
Critical.

------------------------------------------------------------------------

## 2B.15 Scrap / Miscellaneous Issue are moving-average outbound events

These outbound transactions remove value using authoritative current
cost state.

The trace must display:

-   qty removed
-   average cost immediately before posting
-   value removed
-   remaining qty/value
-   reason/document

No user-entered issue price should be represented as authoritative
COGS/value removal.

------------------------------------------------------------------------

## 2B.16 Inventory rollback symmetry must be tested per transaction family

The generic repair plan is not enough.

For each supported Inventory transaction type, tests must prove:

``` text
POST
→ history/facts/state/balance changed
→ ROLLBACK
→ append reversal lineage
→ balance/state restored economically
→ chronology repaired correctly
→ diagnostic health clean
```

Required families:

-   MR
-   Customer Return
-   GRN
-   Vendor Return
-   Transfer
-   Adjustment
-   Misc Issue
-   Scrap

Non-stock GRN must be tested separately because it does not have the
same physical stock semantics.

------------------------------------------------------------------------

## 2B.17 Inventory repair adapter can be concrete

Because Inventory already centralizes posting and rollback through
`IIvInventoryPostingService`, implement:

`InventoryCostingRepairAdapter`

It should map only direct inventory owners to the existing `PostAsync` /
`RollbackAsync` paths: miscellaneous receipt, miscellaneous issue,
scrap, transfer, adjustment, direct customer return, and direct vendor
return. Its `OwnerTypes` are those owners. It must not claim sales-out,
CN-owned customer return, purchase-CN vendor return, goods receipt, or
non-stock goods receipt.

It must still run dependency, chronology, period, epoch, and
preview-staleness gates before calling them, and it must rebuild the
posting command from the current document immediately before the call
as required in section 12.

Do not call private posting cores or repositories directly from the
repair center.

------------------------------------------------------------------------

## 2B.18 Procurement repair adapters must remain document-specific

Recommended adapters:

-   `PurchaseGoodsReceiptRepairAdapter`
    -   the only adapter for goods receipt and non-stock goods receipt
    -   delegates to Inventory GRN rollback/post
    -   understands PO received-quantity side effects
-   Direct vendor return is `InventoryCostingRepairAdapter` only. Do not
    also register `PurchaseVendorReturnRepairAdapter` for that owner.
-   `PurchaseCdnStockRepairAdapter`
    -   vendor return whose `PoCdn.VrBatchNo` proves purchase-CN
        ownership
    -   delegates through `PoCdnService`

Do **not** create a generic `PurchaseInvoiceRevalueAdapter` until a
formal final-invoice inventory revaluation algorithm exists.

Purchase Invoice variance is diagnostic-only under the current reviewed
design.

------------------------------------------------------------------------

# 2C. Inventory + Procurement approval matrix

  -------------------------------------------------------------------------
  Area              Current repo        Diagnostic Center Approval
                    evidence            requirement       
  ----------------- ------------------- ----------------- -----------------
  GRN provisional   PO net price +      Explain and       APPROVED
  cost              GRN-date FX frozen  validate evidence 

  GRN → PO lineage  PO/revision/line    Trace exact       APPROVED
                    stored              source            

  PI matching       PO qty/price        Show matching and APPROVED
                    validation,         variance          
                    InvoicedQty                           

  PI inventory      Not authoritative   Diagnose policy   APPROVED WITH
  revaluation       today               gap; no auto-fix  GUARD

  Vendor Return     Central inventory   Separate          APPROVED
                    stock-out           commercial vs     
                                        valuation price   

  Purchase CN       Delegates to VR     Trace both        APPROVED
  ReturnStock       stock path          financial + stock 
                                        legs              

  Transfer          V2 out/in valuation Prove qty/value   APPROVED
                                        conservation      

  Adjustment        Can use             Show provenance;  APPROVED WITH
                    explicit/fallback   warn fallback     DIAGNOSTIC
                    price                                 

  MR                Inbound valuation   Explain           APPROVED
                    evidence            reason/cost basis 

  Misc Issue/Scrap  Moving-average      Explain value     APPROVED
                    outbound            removal           

  Customer Return   Original-sale cost  Prove restored    APPROVED
                    lineage             original COGS     

  Rollback          Central inventory   Per-family        APPROVED
                    rollback + V2       symmetry tests    
                    reversal                              

  Period close      Quantity +          Hard boundary for APPROVED
                    valuation snapshots repair            

  Pre-V2 history    Epoch/cutover       Coverage warning  APPROVED
                    semantics           / no unsafe       
                                        repair            
  -------------------------------------------------------------------------

# 3. Product design

Create two distinct security and workflow areas.

## A. Costing Diagnostics

Read-only.

Purpose:

-   health check
-   item cost timeline
-   document trace
-   production cost trace
-   reconciliation
-   explanation
-   impact discovery

Normal authorized finance/inventory users may use this.

## B. Costing Repair

Privileged and auditable.

Purpose:

-   prepare repair
-   preview dependencies
-   require reason
-   execute only supported repair action
-   re-run diagnostics
-   record before/after evidence

Do not mix repair buttons directly into every grid row.

------------------------------------------------------------------------

# 4. New menu and permissions

Files that must change together:

-   `ErpWeb.Core/Menus/MenuCodes.cs`
-   `ErpWeb.Core/Menus/PermissionCodes.cs`
-   `ErpWeb/Menus/menus.xml`

Do not edit `ErpWeb/publish/Menus/menus.xml`. That file is publish
output. Runtime menus load from `ErpWeb/Menus/menus.xml` through
`MenuDefinitionService` and `MenuSyncService`.

Add to `MenuCodes.cs`:

``` csharp
public const string InventoryCostingCenter = "INV_COSTING_CENTER";
```

Add under Inventory → Inquiry in `ErpWeb/Menus/menus.xml`:

``` xml
<Menu Code="INV_COSTING_CENTER"
      Name="Costing Diagnostic & Repair Center"
      Route="/inventory/costing-center"
      SortOrder="..."
      Icon="fa-solid fa-stethoscope" />
```

Permissions:

-   `ACCESS`
-   `VIEW_COST`
-   `REPAIR_COST`

Do not use `VIEW_PRICE` as the gate for inventory valuation, COGS, WIP
value, FG cost, or repair preview. `VIEW_PRICE` is commercial/sales
price visibility. Production cost-facing services already use
`PermissionCodes.ViewCost`.

`REPAIR_COST_OVERRIDE` stays future-only. Do not seed it until a real
controlled use exists.

`ACCESS` alone must not expose monetary cost values. The service layer
must enforce permissions; UI hiding is not sufficient.

`REPAIR_COST` does not exist yet. Add it and include it in
`PermissionCodes.All`:

``` csharp
public const string RepairCost = "REPAIR_COST";
```

`MenuSyncService` attaches only `ACCESS` to a newly inserted XML menu.
Explicitly seed:

-   `Permission` row `REPAIR_COST`
-   `INV_COSTING_CENTER` → `ACCESS`
-   `INV_COSTING_CENTER` → `VIEW_COST`
-   `INV_COSTING_CENTER` → `REPAIR_COST`

Privilege rule: a user who executes a repair must also hold the
original module permission required by the authoritative service.
Examples: Inventory `ROLLBACK` / `POST`, Sales Invoice `ROLLBACK` /
`POST`, Delivery Order `ROLLBACK` / `POST`, Planning finished-good
`ROLLBACK` / `POST`. `REPAIR_COST` must not bypass those rights.

Route: `/inventory/costing-center`

Title: **Costing Diagnostic & Repair Center**

## 4A. Sales stock ownership

This matrix is a first-class input to the dependency graph. Adapters
resolve by the `OwnerType` from section 2A.4, not by raw
`StockPosting.SourceDocumentType` and not by UI text. A sales-out
posting stays an inventory sales-out batch; `DoNo` selects the delivery
order adapter and `InvNo` selects the invoice adapter.

-   Direct invoice: physical owner and financial owner are the invoice.
    `SaInvoiceService` posts stock only for non-`LinkDo` stock-controlled
    lines. Adapter: `SalesInvoiceCostingRepairAdapter` calling
    `ISaInvoiceService.RollbackAsync` / `PostAsync`.
-   DO → invoice: physical owner is `SA_DO`; financial owner is the
    invoice. `GetInvoiceCogsAsync` already selects `SA_DO` when
    `LinkDo` is true. The invoice adapter must not reverse DO stock.
    Route that stock through `SalesDeliveryOrderCostingRepairAdapter`
    and `ISaDoService.RollbackAsync` / `PostAsync`, including the
    downstream invoice dependencies that service already enforces.
-   Credit note without stock return: no physical movement. Financial
    owner is the credit note.
-   Credit note with stock return: physical owner is the Customer
    Return stock leg; financial owner is the credit note. Adapter:
    `SalesCreditNoteCostingRepairAdapter` calling
    `ISaCdnService.RollbackAsync` / `PostAsync`. Do not post or roll
    back the Customer Return batch directly.
-   DO Force Close: not a stock reversal, not a cost reversal, and not
    a repost candidate. `SaDoService.ForceCloseAsync` leaves shipped
    stock in place, writes off uninvoiced quantity, and stamps the
    shipment tombstone. Classify it as a lifecycle state only.

------------------------------------------------------------------------

# 5. UI structure

Use the existing Inventory UI language (`iv-page`, `iv-hero`, chips,
cards, DevExpress grid, standard toolbar).

Do not create a new visual pattern.

## Tab 1 --- Health

Filters:

-   As-of date / date range
-   Item
-   Warehouse
-   Location
-   Lot
-   Source module
-   Source document
-   Severity
-   Finding code
-   Valuation status

KPI cards:

-   Critical
-   Errors
-   Warnings
-   Unvalued movements
-   Quantity/value mismatches
-   Production cost issues
-   Reversal issues
-   Items affected
-   Documents affected

Grid:

  Column          Purpose
  --------------- -----------------------------------------
  Severity        Critical/Error/Warning/Info
  Code            Stable machine-readable diagnostic code
  Item            Affected item
  Warehouse/Lot   Costing scope
  Date            First relevant date
  Document        Root/source document
  Problem         User-readable explanation
  Expected        Expected invariant/value
  Actual          Current value
  Impact          Number of later facts/docs
  Action          Trace / Preview Repair

------------------------------------------------------------------------

## Tab 2 --- Item Cost Explorer

For one item, show a chronological valuation timeline.

Columns:

-   posting sequence
-   effective date/time
-   source module
-   document type/no/line
-   movement
-   warehouse/location/lot
-   qty in
-   qty out
-   quantity after
-   value in
-   value out
-   inventory value after
-   unit cost used
-   average cost after
-   valuation source
-   valuation status
-   reversal state

The running values must be derived from sealed `StockValuationFact` rows
in posting order.

Do not use current `IvBalLoc.UnitPrice` as historical truth.

------------------------------------------------------------------------

## Tab 3 --- Cost Explanation

Selecting a valuation fact opens an explanation panel.

For current Moving Average:

### Receipt example

    Before receipt
    Qty       100
    Value     RM 1,000.00
    Avg cost  RM 10.000000

    Receipt GRN-000123
    Qty       50
    Value     RM 650.00
    Unit cost RM 13.000000

    After receipt
    Qty       150
    Value     RM 1,650.00
    Avg cost  RM 11.000000

### Issue example

    Before issue
    Qty       150
    Value     RM 1,650.00
    Avg cost  RM 11.000000

    Sales issue
    Qty       20
    COGS      RM 220.00

    After issue
    Qty       130
    Value     RM 1,430.00
    Avg cost  RM 11.000000

For final depletion, explicitly explain that the engine takes the exact
remaining inventory value to prevent rounding residue.

Also show:

-   valuation source
-   price evidence
-   original/reversal fact
-   posting ID
-   history ID
-   source snapshot hash/revision
-   posted by / posted time

Technical IDs should be under an expandable **Technical evidence**
section, not the primary user display.

------------------------------------------------------------------------

## Tab 4 --- Document Cost Trace

Search by document number.

Supported source types should be derived from actual
`StockPosting.SourceDocumentType` values, not a hard-coded UI
assumption.

Trace:

`Business document → StockPosting → IvTrxHistory → StockValuationFact → StockCostState impact`

For sales invoice COGS, reuse/extend:

`IStockValuationQueryService.GetInvoiceCogsAsync`

Show unresolved lines clearly.

------------------------------------------------------------------------

## Tab 5 --- Production Cost Trace

Trace the full production chain.

Display:

1.  Work Order
2.  Issue to Production
3.  Inventory valuation fact used for RM
4.  Production material movement
5.  Production balance lot
6.  Daily Production consumption
7.  WIP output value
8.  downstream process handoff
9.  Finished Good Receipt
10. exact value transferred to inventory
11. FG inventory valuation fact
12. later sale/COGS if applicable

This tab should reuse production allocation/dependency evidence rather
than infer links from matching item/date.

------------------------------------------------------------------------

## Tab 6 --- Repair Queue

Only visible with `REPAIR_COST`.

Each repair case has:

-   finding
-   root cause
-   affected item(s)
-   earliest affected posting sequence/date
-   dependent postings
-   closed-period status
-   production dependencies
-   proposed repair strategy
-   before totals
-   expected after totals
-   required reversals
-   risk level
-   reason
-   confirmation

------------------------------------------------------------------------

# 6. Diagnostic service architecture

Add:

`ErpWeb.Core/Costing/ICostingDiagnosticService.cs`

`ErpWeb.Core/Costing/CostingDiagnosticService.cs`

Suggested result contracts:

``` csharp
public enum CostingFindingSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed record CostingFinding(
    string Code,
    CostingFindingSeverity Severity,
    bool IsBlocking,
    string? ItemCode,
    string? WarehouseCode,
    string? LocationCode,
    string? LotNo,
    DateTime? EffectiveAt,
    string? SourceDocumentType,
    string? SourceDocumentNo,
    long? StockPostingId,
    long? ValuationFactId,
    string Summary,
    string Explanation,
    decimal? ExpectedQty,
    decimal? ActualQty,
    decimal? ExpectedValue,
    decimal? ActualValue,
    string RecommendedAction);
```

Use stable finding codes. UI must never branch on message text.

`ItemCode`, warehouse, location, and lot are optional. CD-001, period
or snapshot corruption, repair orchestration failure, epoch
configuration, and security or configuration findings may have no
single item.

------------------------------------------------------------------------

# 7. Mandatory health checks

## CD-001 --- Unsealed posting

Detect `StockPosting` rows that remain unsealed beyond the expected
transaction boundary.

Severity: **Critical**

Message:

> A stock posting was started but not sealed. Costing may be incomplete.
> Do not manually change the stock rows; run posting reconciliation.

------------------------------------------------------------------------

## CD-002 --- V2 history without valuation fact

For financially relevant sealed V2 `IvTrxHistory`, verify authoritative
`StockValuationFact` exists.

Severity: **Critical**

Exclude history rows that are legitimately non-financial according to
the same predicate used by `InventoryValuationService`.

------------------------------------------------------------------------

## CD-003 --- Valuation fact without valid sealed posting

Every active fact must belong to the correct tenant and a sealed
posting.

Severity: **Critical**

------------------------------------------------------------------------

## CD-004 --- Cost-state quantity mismatch

Rebuild expected current quantity from active sealed valuation facts and
compare with `StockCostState.OnHandBaseQty`.

Severity: **Critical**

------------------------------------------------------------------------

## CD-005 --- Cost-state value mismatch

Rebuild expected signed value from active sealed valuation facts and
compare with `StockCostState.InventoryValue`.

Severity: **Critical**

Use the same money rounding policy as the valuation engine.

------------------------------------------------------------------------

## CD-006 --- Cost-state average mismatch

If qty \> 0:

`expected average = inventory value / on-hand base qty`

Compare to `StockCostState.AverageUnitCost`.

If qty == 0, value and average must reconcile to zero according to
current engine invariants.

Severity: **Error/Critical** depending on delta.

------------------------------------------------------------------------

## CD-007 --- Negative valuation quantity/value

Detect invalid negative cost-state quantity/value or impossible
valuation facts.

Severity: **Critical**

------------------------------------------------------------------------

## CD-008 --- Zero/missing receipt cost evidence

Detect financially relevant receipt where cost evidence/source is
missing or cannot be explained.

Use the same resolution rules as `InventoryValuationService`; do not
invent a second costing precedence.

Severity: **Critical** if already posted; **Error** if draft/pre-post.

------------------------------------------------------------------------

## CD-009 --- Unvalued valuation status

Detect active `UNVALUED` stock or production movements.

Severity: **Critical**

------------------------------------------------------------------------

## CD-010 --- Reversal lineage broken

Validate:

-   reversal points to original
-   original exists
-   original is sealed
-   no duplicate active reversal
-   valuation reversal matches history/posting reversal
-   tenant/branch identity agrees

Severity: **Critical**

------------------------------------------------------------------------

## CD-011 --- Backdated dependency

Detect a movement whose repair/repost would cross later active valuation
facts.

Use the same chronology concept as `RejectBackdatedPoolsAsync`.

Severity: **Error**

Explanation must tell user that later transactions must be
reversed/reposted in dependency order.

------------------------------------------------------------------------

## CD-012 --- Quantity ledger mismatch

Reuse `IIvInventoryReconciliationService`.

Do not duplicate its calculation.

Severity mapping:

-   blocking inventory integrity finding → Critical/Error
-   advisory finding → Warning

------------------------------------------------------------------------

## CD-013 --- IvBalLoc price evidence missing

Where a balance is expected to be cost-ready, detect missing `UnitPrice`
/ `PriceEvidence` using
`ProductionCostReadiness.HasVerifiedInventoryCost`.

Severity: **Error**

Especially important for Issue to Production readiness.

------------------------------------------------------------------------

## CD-014 --- Production pool unvalued

Reuse the rules represented by
`ProductionCostReadiness.PoolValuationError`.

Severity: **Critical**

------------------------------------------------------------------------

## CD-015 --- Production pool tracked quantity mismatch

Compare production pool live quantity with valuation tracking quantity.

Severity: **Critical**

------------------------------------------------------------------------

## CD-016 --- Production pool tracked value mismatch

Compare:

-   `ProductionBalLot.TotalCost`
-   tracked production valuation value

Severity: **Critical**

------------------------------------------------------------------------

## CD-017 --- Zero-qty/non-zero-value residue

Inventory or production cost pool with zero quantity but non-zero value.

Severity: **Critical**

------------------------------------------------------------------------

## CD-018 --- FG exact transfer mismatch

For Finished Good Receipt verify:

-   exact source-pool allocated value
-   `IvTrxHistory.ExactTransferredValue`
-   FG production fact value
-   resulting valuation fact value
-   destination price evidence

Severity: **Critical**

This protects the exact-value design already implemented in FG posting.

------------------------------------------------------------------------

## CD-019 --- Production contribution/dependency gap

Detect active production consumption/output for which
contribution/dependency evidence is incomplete.

Severity: **Error/Critical**

------------------------------------------------------------------------

## CD-020 --- Period snapshot mismatch

For closed periods, compare current immutable valuation evidence up to
the snapshot watermark/hash against stored
`StockValuationPeriodSnapshot`.

Severity: **Critical**

Never silently regenerate a historical closed snapshot.

------------------------------------------------------------------------

## CD-021 --- Source snapshot/document revision inconsistency

Detect posting whose source evidence/revision cannot be reconciled with
its immutable posting evidence.

Severity: **Error**

Do not flag a legitimate later document revision if the original
posting/reversal lineage is intact.

------------------------------------------------------------------------

## CD-022 --- Sales COGS unresolved

Extend `GetInvoiceCogsAsync`. Reuse alone is not diagnostic proof.

On HEAD `5b7478b4` the query resolves `SA_INVOICE` or `SA_DO` from
`LinkDo`, prefers an exact source line, otherwise falls back to owner
plus item, and sets `IsResolved` when any matching fact exists. That
fallback must not be used for this center.

For every posted stock-controlled invoice line prove:

``` text
expected base quantity
==
sum of active outbound StockValuationFact.BaseQty
```

for the exact owner type, owner document, owner line, and item, on a
sealed posting, valued, and not reversed. Also prove the cost amount.

If that lineage cannot be proven, classify
`LEGACY_OR_AMBIGUOUS_COGS_LINEAGE` and do not present the line as
proven COGS.

Severity: **Critical** for financial reporting.

## CD-039 --- Sales COGS ownership/quantity lineage incomplete

A posted financial document whose COGS cannot be proven by the exact
owner, line, and quantity invariant above.

Severity: **Critical**

------------------------------------------------------------------------

## CD-023 --- Suspicious cost jump

Informational/anomaly rule, not an accounting invariant.

Example threshold:

-   absolute % change from prior moving average
-   configurable threshold, default e.g. 50%

Severity: **Warning**

Never auto-repair this finding.

The price may be legitimate.

------------------------------------------------------------------------

## CD-024 --- Costing method unsupported by authoritative engine

If item/company configuration claims FIFO or Standard while
authoritative V2 fact/state engine is Moving Average, show an explicit
configuration/implementation warning.

Severity: **Critical** if transactions are being posted under a
misleading configured method.

This prevents reports from claiming FIFO/Standard when the authoritative
ledger is not actually using that algorithm.

------------------------------------------------------------------------

# 8. Cost trace calculation

Add a read-only query service:

`ICostingTraceService`

`CostingTraceService`

It must reconstruct the timeline from:

-   sealed `StockPosting`
-   `StockValuationFact`
-   linked `IvTrxHistory`

Ordering:

1.  `PostingSequence`
2.  `PostingLineNo`
3.  `SplitOrdinal`

Do not order only by document date.

The authoritative running state is the branch + item + cost method
pool. Compute that state from all in-scope sealed facts first. A
warehouse, location, or lot filter changes only which rows are
displayed. It must not recompute `RunningAvg` from the filtered subset.

For a bounded or current-period trace, do not start at quantity 0 and
value 0 when the item already had stock. Load an opening anchor from
evidence immediately before the requested start:

1.  Prior trusted valuation period snapshot, when one applies.
2.  Otherwise reconstruct from sealed `StockValuationFact` rows as of
    that start.
3.  Never use current `IvBalLoc.UnitPrice` as the historical opening.

The anchor supplies `OpeningQty`, `OpeningValue`, and
`OpeningAverage`. Then apply in-period facts:

``` text
RunningQty   = OpeningQty
RunningValue = OpeningValue
RunningQty   += Direction * BaseQty
RunningValue += Direction * CostAmount
RunningAvg    = RunningQty == 0 ? 0 : RunningValue / RunningQty
```

Use the same 6-decimal monetary/valuation rounding policy as the current
valuation engine where appropriate.

A trace must identify exact reversal legs and visually pair them with
originals.

Before any of these totals are called authoritative across an epoch
change, resolve the epoch gate in section 8A.

------------------------------------------------------------------------

# 8A. Ledger epoch gate

`ProductionStockHistoryService` filters by the active
`StockLedgerEpoch`. `StockValuationQueryService.GetAsOfAsync`,
`GetInvoiceCogsAsync`, and `StockValuationSnapshotBuilder` fact queries
do not predicate on `LedgerEpochId`. The snapshot header stores the
active epoch id, but the fact and watermark queries are branch and date
scoped.

Before Costing Center totals, COGS proof, cost-state rebuild,
snapshots, or repair planning are declared authoritative, tests with a
`RETIRED` epoch and an `ACTIVE` epoch, and facts in both, must prove
one model:

-   **Model A — cumulative branch ledger.** Facts from retired and
    active epochs intentionally form one accounting history.
-   **Model B — active epoch scoped.** Only facts on the active epoch
    participate in current V2 monetary truth.

Do not guess. After the tests choose a model, every diagnostic, COGS
query, state rebuild, snapshot, and repair plan follows that same rule.
Until then, do not claim mathematically authoritative V2 totals across
an epoch transition.

------------------------------------------------------------------------

# 9. Repair design

## 9.1 Principle

**Diagnose automatically. Repair conservatively.**

Initial release should support fewer repair types correctly rather than
provide a dangerous generic "Recalculate All".

------------------------------------------------------------------------

## 9.2 Repair mode A --- Guided reversal/repost

This should be the primary Phase 1 repair mechanism because it matches
the current V2 append-only architecture.

Workflow:

1.  User selects finding.
2.  System resolves root posting.
3.  System discovers all later active dependents.
4.  System checks period status.
5.  System checks production dependencies.
6.  System builds a reverse-order rollback plan.
7.  Show preview.
8.  User enters reason.
9.  Authorized user confirms.
10. Resolve every step through a registered `ICostingRepairAdapter`.
11. Execute proven module rollback operations from newest dependent to
    root.
12. Stop before execution if any node has no safe registered adapter.
13. Correct source/master evidence if user action is required.
14. Repost root-to-dependent using the same authoritative module
    services.
15. Re-run health checks.
16. Mark repair case successful only when blocking findings are gone.

Steps 11 and 14 are saga steps under section 12. Each module call
commits on its own. A failure after an earlier commit stops the case
as `PARTIAL` or `NEEDS_ATTENTION`.

Do not bypass each module's rollback rules. A chain containing an
unsupported document type is preview-only and must return
`CanRepair = false`.

------------------------------------------------------------------------

## 9.3 Repair mode B --- Rebuild derived cost state

A controlled rebuild of `StockCostState` may be added only if it is
proven that:

-   immutable valuation facts are correct
-   only the derived state is corrupt
-   no valuation facts need changing
-   no closed-period invariant is violated

Create a dedicated domain service.

Do not put rebuild SQL in the Razor page.

The service must:

1.  lock branch
2.  establish a posting-sequence watermark
3.  calculate expected state from sealed, non-reversed facts selected
    under the resolved section 8A epoch model
4.  compare before/after
5.  abort if facts themselves are inconsistent
6.  write an audit record
7.  update derived state transactionally
8.  rerun diagnostics

------------------------------------------------------------------------

## 9.4 Repair mode C --- Future valuation replay

Do **not** implement arbitrary replay in Phase 1.

A future replay engine may:

-   select earliest affected sequence/date
-   replay immutable source evidence
-   create adjustment/reversal facts
-   never mutate sealed historical facts
-   respect closed periods

This requires a formal accounting policy and should be a later phase.

------------------------------------------------------------------------

# 10. Repair preview / impact analyzer

Add:

`ICostingRepairPlanner`

`CostingRepairPlanner`

Input:

-   finding ID/context
-   root posting/fact

Output:

``` csharp
public sealed record CostingRepairPlan(
    bool CanRepair,
    string Strategy,
    string? BlockingReason,
    IReadOnlyList<CostingRepairStep> Steps,
    IReadOnlyList<string> AffectedItems,
    IReadOnlyList<string> AffectedDocuments,
    DateTime? EarliestEffectiveAt,
    DateTime? LatestEffectiveAt,
    int PostingCount,
    int ValuationFactCount,
    IReadOnlyList<CostingRepairPoolImpact> PoolImpacts);

Each repair step shows three separate fields: physical stock source,
business repair owner (`OwnerType` and document number), and the
selected adapter. Do not collapse those into `StockPosting.SourceDocumentType`.

public sealed record CostingRepairPoolImpact(
    string ItemCode,
    string CostMethod,
    string BaseUom,
    decimal BeforeQty,
    decimal AfterQty,
    decimal BeforeValue,
    decimal ExpectedValue,
    decimal DeltaValue);
```

Do not put one `CurrentValue` / `ExpectedValue` on the plan. A repair
can span items, base UOMs, document families, and production pools.
Report impact per item and cost pool. Total company-base currency only
when every row is confirmed company-base currency. Never total
quantities across items or UOMs.

The preview must detect:

-   closed period
-   later stock postings
-   active reversal dependencies
-   production pool consumers
-   FG receipts
-   later sales/COGS
-   cross-document dependencies

If dependency resolution is incomplete, **CanRepair = false**.

Never guess.

------------------------------------------------------------------------

# 11. Repair audit

Use two entities. Do not store lifecycle fields on one "immutable" row.

`CostingRepairCase` is the mutable lifecycle record:

-   Id
-   RepairRequestId
-   CompanyCode
-   BranchCode
-   RootFindingCode
-   RootStockPostingId
-   RootValuationFactId
-   Strategy
-   PreviewHash
-   Status (`PARTIAL` or `NEEDS_ATTENTION` when a later step fails)
-   RequestedBy
-   RequestedAtUtc
-   StartedAtUtc
-   CompletedAtUtc
-   Reason
-   RowVersion

`CostingRepairAuditEvent` is append-only. Reject update and delete.

Event types include `STEP_PREPARED`, `STEP_COMPLETED`, `STEP_FAILED`,
and `RECOVERED_COMPLETION`.

Fields, nullable where a module does not return the value:

-   Id
-   RepairCaseId
-   Sequence
-   EventType
-   StepNo
-   StableStepId
-   AttemptNo
-   Module
-   DocumentType
-   DocumentNo
-   DesiredAction
-   ModuleRequestId
-   BeforeDocumentVersion
-   AfterDocumentVersion
-   BeforeStatus
-   AfterStatus
-   ResultStockPostingId
-   ResultOperationId
-   DependencyFingerprint
-   PostingWatermark
-   EvidenceJson
-   CreatedAtUtc
-   CreatedBy

Persist both through `AppDbContext` `DbSet`s, EF configurations, and a
migration. Configurations are applied by `ApplyConfigurationsFromAssembly`.

Indexes:

-   `CompanyCode + BranchCode + RepairRequestId` unique
-   `CompanyCode + BranchCode + Status`
-   `RootStockPostingId`
-   `RepairCaseId + Sequence` unique

------------------------------------------------------------------------

# 12. Concurrency and safety

Cross-module repair is a durable saga. It is not one outer database
transaction. Public services own their own transactions, including
`IIvInventoryPostingService`, `ISaInvoiceService`, `ISaDoService`,
`ISaCdnService`, `IPoCdnService`, `IProductionMaterialIssueService`,
`IProductionOutputService`, and
`IProductionFinishedGoodReceiptService`. A chain such as sale → FG →
daily production → issue to production → GRN cannot be wrapped in one
transaction while calling those APIs.

The preview may persist only document identity, intended action,
dependency fingerprint, posting watermark, expected lifecycle state,
reason, and a stable repair-step id. It must not persist a module
command. Sales and purchase requests carry `RowVersion`.
`FinishedGoodReceiptCommand` carries `ExpectedVersion` and `RequestId`.
Production output and material issue use the same kind of version or
request token where those APIs require one. A command built at preview
time is stale by the time the saga runs.

Immediately before each saga step:

1.  Append `STEP_PREPARED` with the case id, step number, stable step
    id, module, document type and number, desired action, expected
    before-state, dependency fingerprint, posting watermark, and
    `ModuleRequestId` when that API supports one.
2.  Reload the document through the authoritative service or
    repository contract.
3.  Confirm tenant and branch, and confirm the expected lifecycle
    state.
4.  Read the current `RowVersion` or `ExpectedVersion`.
5.  Re-check the dependency graph, period close, and the section 8A
    epoch rule.
6.  If the document version or lifecycle changed after preview, stop
    with `STALE_PREVIEW` / `REPLAN_REQUIRED`. Do not silently refresh
    and continue.
7.  Build the module command from that current state and execute it
    immediately. Let that service commit its own transaction.
8.  On success, append `STEP_COMPLETED` with the resulting status and
    version, `StockPostingId`, and the module operation or request id
    when available.
9.  On a module exception, append `STEP_FAILED`. A failure inside the
    module rolls back only that module transaction.
10. Re-read ledger state, rerun the relevant diagnostics, and continue
    only if still safe.

If step N fails after earlier steps committed, set
`CostingRepairCase.Status` to `PARTIAL` or `NEEDS_ATTENTION`. Do not
claim the earlier module commits were rolled back. Do not
auto-compensate unless a planned reverse operation is still valid.
Resume only after full revalidation.

Restart rule: if `STEP_PREPARED` exists without `STEP_COMPLETED`,
reconcile authoritative document and ledger state before any retry.

-   Desired result already present: append `RECOVERED_COMPLETION` and
    continue.
-   Action clearly did not commit: rebuild a fresh command and retry
    only when that retry is still safe.
-   Ambiguous: set `NEEDS_ATTENTION` and do not retry automatically.

Do not treat duplicate-request idempotency as true for every module.
Production paths that take a request id can use that id. Sales
rollback is state and `RowVersion` driven: once a rollback has
committed, the document is no longer `POSTED`, and a second rollback
is not a safe retry. The saga supplies orchestration idempotency.
The module API does not.

------------------------------------------------------------------------

# 13. Integration with period close

Phase 1:

-   Add a **Cost Health** link/button on period close.
-   Show number of critical costing findings.
-   Do not yet block close on every new diagnostic rule.

Phase 2 after validation:

Block period close for a small explicit list:

-   V2 history missing valuation
-   valuation/state quantity mismatch
-   valuation/state value mismatch
-   unvalued active movement
-   broken reversal lineage
-   production quantity/value mismatch
-   FG exact transfer mismatch
-   closed-period valuation snapshot mismatch
-   unresolved posted sales COGS

Warnings such as abnormal price change must never block close.

Repair that would change a closed period may run only after
`IIvPeriodCloseService.ReopenAsync` has reopened every later closed
period first, then the target period. The planner rebuilds the preview
after reopen. Execution is allowed only while that period is
legitimately open. Re-close creates a new snapshot revision. Prior
valuation snapshot revisions stay. The repair service must not delete
or rewrite valuation snapshots, and it must not reopen a period itself.

------------------------------------------------------------------------

# 14. Integration with existing reconciliation pages

Do not remove:

-   `/inventory/reconciliation`
-   Production stock reconciliation

Instead:

-   Costing Center Health should aggregate their relevant findings.
-   Provide **Open Inventory Reconciliation** and **Open Production
    Reconciliation** actions.
-   Keep those pages useful for focused operational diagnosis.

The Costing Center is the cross-module financial-cost view.

------------------------------------------------------------------------

# 15. Service registration

Update:

`ErpWeb.Core/CoreServiceCollectionExtensions.cs`

Register:

``` csharp
services.AddScoped<ICostingDiagnosticService, CostingDiagnosticService>();
services.AddScoped<ICostingTraceService, CostingTraceService>();
services.AddScoped<ICostingRepairPlanner, CostingRepairPlanner>();
services.AddScoped<ICostingRepairService, CostingRepairService>();
services.AddScoped<ICostingRepairOwnershipResolver, CostingRepairOwnershipResolver>();
services.AddScoped<ICostingMethodDiagnosticProvider, MovingAverageCostingDiagnosticProvider>();
services.AddScoped<ICostingRepairAdapter, InventoryCostingRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, SalesInvoiceCostingRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, SalesDeliveryOrderCostingRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, SalesCreditNoteCostingRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, PurchaseGoodsReceiptRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, PurchaseCdnStockRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, ProductionMaterialIssueRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, ProductionOutputRepairAdapter>();
services.AddScoped<ICostingRepairAdapter, ProductionFinishedGoodReceiptRepairAdapter>();
```

The planner resolves `ICostingRepairOwnershipResolver` first, then
finds the one adapter whose `OwnerTypes` contains that `OwnerType`.
Zero matches stays preview-only. More than one match is a registration
defect and must fail closed.

Do not inject `AppDbContext` directly into the Razor page for diagnostic
or repair logic.

------------------------------------------------------------------------

# 16. Suggested file structure

``` text
ErpWeb.Core/
  Costing/
    ICostingDiagnosticService.cs
    CostingDiagnosticService.cs
    ICostingTraceService.cs
    CostingTraceService.cs
    ICostingRepairPlanner.cs
    CostingRepairPlanner.cs
    ICostingRepairService.cs
    CostingRepairService.cs
    CostingFindingCodes.cs
    CostingDiagnosticResults.cs
    CostingTraceResults.cs
    CostingRepairResults.cs

ErpWeb.Model/
  Data/
    AppDbContext.cs
  Entities/
    Costing/
      CostingRepairCase.cs
      CostingRepairAuditEvent.cs
  Configurations/
    Costing/
      CostingRepairCaseConfiguration.cs
      CostingRepairAuditEventConfiguration.cs
  Migrations/
    (EF Core migration for the two costing repair tables)

ErpWeb.Core/Menus/MenuCodes.cs
ErpWeb.Core/Menus/PermissionCodes.cs
ErpWeb/Menus/menus.xml

ErpWeb.UI/
  Inventory/
    Inquiry/
      IvCostingCenter.razor
      IvCostingCenter.razor.cs
      IvCostingCenter.razor.css
```

If the project convention prefers Inventory rather than a new Costing
namespace for model entities, follow the existing project convention
consistently.

------------------------------------------------------------------------

# 17. User-facing explanation rules

Every finding must contain:

### What happened

Business language.

### Why it matters

Effect on stock value, COGS, GP, production cost, or period close.

### Evidence

Document/item/date/value.

### Recommended action

Concrete next step.

Bad:

> `VALUATION_REQUIRED`

Good:

> **Item RM001 has no approved cost before this issue.**\
> Issue to Production IP-000128 attempted to consume 20 PCS on
> 05/10/2026, but the costing ledger has no approved opening or receipt
> value for RM001 before that posting. Production cost cannot be trusted
> until the upstream inventory receipt/opening cost is repaired.

Technical error code remains visible in details.

------------------------------------------------------------------------

# 18. No direct cost editing

The following UI must **not** be implemented:

-   editable Unit Cost grid
-   editable Cost Amount
-   editable Average Cost
-   editable valuation facts
-   editable production pool TotalCost
-   "Set cost to ..."
-   "Force balance"
-   "Delete valuation row"
-   SQL repair textbox

If an exceptional manual valuation adjustment is eventually required,
implement it as a formal posted adjustment document with approval,
reason, immutable evidence, and accounting impact.

------------------------------------------------------------------------

# 19. FIFO and Standard Cost future compatibility

The diagnostic framework should use a method adapter contract, for
example:

``` csharp
public interface ICostingMethodDiagnosticProvider
{
    string CostMethod { get; }
    Task<CostingMethodTrace> ExplainAsync(...);
    Task<IReadOnlyList<CostingFinding>> DiagnoseAsync(...);
}
```

Initial provider:

`MovingAverageCostingDiagnosticProvider`

Future:

-   `FifoCostingDiagnosticProvider`
-   `StandardCostingDiagnosticProvider`

### Future FIFO trace must show

-   receipt layer
-   original qty
-   remaining qty
-   receipt unit cost
-   issue allocation by layer
-   exact layer depletion
-   reversal of layer consumption
-   orphan/negative layer checks

### Future Standard Cost trace must show

-   active standard cost/version
-   effective date
-   transaction standard value
-   actual receipt cost
-   purchase/production variance
-   variance account/evidence if accounting integration exists
-   standard-cost change audit

Do not implement fake FIFO by sorting `IvBalLoc` lots if the
authoritative ledger has not implemented FIFO layers.

------------------------------------------------------------------------

# 20. Performance requirements

Do not load the entire valuation ledger into the browser.

Health service must support server-side filters and bounded result sets.

Recommended:

-   default current open period
-   optional item/warehouse/date filters
-   summary query first
-   drill into details on demand
-   indexes reviewed for:
    -   tenant + item + posting sequence
    -   StockPosting + sealed state
    -   source document identity
    -   InventoryHistoryId
    -   ReversesValuationFactId
    -   OriginalValuationFactId
    -   EffectiveAt
    -   period key

Large cross-company scans are forbidden.

All queries must enforce company/branch scope.

------------------------------------------------------------------------

# 21. Required automated tests

## Diagnostic tests

-   healthy moving-average item → no critical findings
-   V2 history missing valuation → CD-002
-   fact with unsealed posting → CD-003
-   corrupted cost-state qty → CD-004
-   corrupted cost-state value → CD-005
-   incorrect average → CD-006
-   zero qty/non-zero value → CD-017
-   broken reversal link → CD-010
-   unresolved invoice COGS → CD-022
-   production pool value mismatch → CD-016
-   FG exact value mismatch → CD-018

## Trace tests

Use:

1.  opening/receipt
2.  second receipt at different price
3.  issue
4.  transfer
5.  customer return
6.  reversal

Verify running qty/value/average after every fact.

## Inventory posting/rollback symmetry tests

For each Inventory transaction family, capture balance, history,
valuation facts, cost state and related PO quantities before post, after
post, and after rollback.

Required cases:

-   GRN
-   Non-stock GRN
-   MR
-   Customer Return
-   Misc Issue
-   Scrap
-   Vendor Return
-   Transfer
-   positive Adjustment
-   negative Adjustment

For GRN also prove `RecvQty`/`BalanceQty` symmetry. For Vendor Return
prove `ReturnQty` symmetry where linked to PO. For Purchase CN
ReturnStock prove both Purchase-document state and VR stock state remain
synchronized.

## Security tests

-   no ACCESS → denied
-   ACCESS without VIEW_COST → monetary cost values hidden/denied
-   VIEW_PRICE alone does not reveal inventory cost, COGS, WIP, or FG cost
-   no REPAIR_COST → cannot invoke repair endpoint/service
-   REPAIR_COST without the source module ROLLBACK / POST → denied
-   cross-company/cross-branch IDs → rejected
-   new menu XML sync creates ACCESS only; VIEW_COST and REPAIR_COST
    exist only after the explicit seed

## Repair tests

-   stale preview → rejected
-   closed period → rejected until authoritative reopen, then preview rebuilt
-   later dependency → included or repair rejected
-   DO Force Close is not a rollback or repost step
-   single module operation failure → that module transaction rolls back
-   multi-step failure after earlier commits → execution stops, prior
    steps stay audited, status is PARTIAL or NEEDS_ATTENTION, resume
    requires full revalidation
-   successful repair → diagnostics rerun and audit events completed
-   sales-out with invoice number only → sales invoice adapter
-   sales-out with delivery-order number → delivery-order adapter
-   direct customer return → inventory customer-return owner
-   customer return proven by `SaCdnCrLock` → sales credit-note adapter
-   direct vendor return → inventory vendor-return owner
-   vendor return proven by `PoCdn.VrBatchNo` → purchase CN stock adapter
-   goods receipt and non-stock goods receipt → purchase goods-receipt
    adapter, not the generic inventory adapter
-   unproven owner → `CanRepair = false`
-   preview, then another user changes the document `RowVersion` or
    `ExpectedVersion`, then execution is `STALE_PREVIEW` /
    `REPLAN_REQUIRED` and no module action runs. Cover Sales Invoice,
    Delivery Order, Sales CN, Purchase CN, Finished Good Receipt, and
    versioned Production Output / Material Issue
-   crash after the module commit and before `STEP_COMPLETED` →
    restart reconciles state; it does not call rollback again when the
    document is already reversed
-   duplicate saga step is idempotent at the orchestrator. Do not
    assume the underlying Sales rollback API is retry-idempotent

## Integrated scenarios

Run Inventory, Procurement, Sales, Production, StockLedger, Period
Close, Menus/Security, and the new Costing tests.

-   PO → GRN → PI provisional/final variance diagnostic, with no
    automatic revaluation
-   PO → GRN → Vendor Return
-   Purchase CN with ReturnStock
-   direct invoice stock issue and exact COGS quantity proof
-   DO → invoice, COGS owner is the DO
-   Sales CN plus Customer Return restores original COGS
-   RM → issue to production → daily production → WIP → FG → sale → COGS
-   prior-period stock plus current-period receipt and issue begins
    from the opening anchor
-   ACTIVE plus RETIRED epoch facts follow the chosen epoch model
-   closed period → reopen later periods first → reopen target →
    rebuild preview → repair → re-close → new snapshot revision

------------------------------------------------------------------------

# 22. Implementation phases

## Phase 1 --- Read-only Costing Doctor

Proceed only after this plan's permission, menu, exact COGS proof,
epoch gate, branch-item pool scope, and bounded-trace opening anchor
are followed. Do not start Phase 3 automated repair until the saga,
audit split, privilege chaining, and partial-failure tests exist.

Implement first:

-   menu/security using VIEW_COST and the explicit REPAIR_COST seed
-   health dashboard
-   stable finding codes
-   V2 monetary integrity checks
-   reuse Inventory Reconciliation
-   production valuation checks
-   item cost explorer
-   document trace
-   sales COGS trace
-   production cost trace
-   user-readable explanations

**No repair execution yet.**

Acceptance gate:

A finance/inventory user can identify the first bad document and
understand why cost became invalid without SQL or Visual Studio.

------------------------------------------------------------------------

## Phase 2 --- Repair Preview

Start only after the Sales ownership matrix in section 4A and the
epoch model in section 8A are fixed. No execution in this phase.

Implement:

-   dependency graph, including the section 2A.4 ownership resolver,
    the Sales owner matrix, and Force Close exclusion
-   each preview step shows physical stock source, business repair
    owner, and selected adapter separately
-   earliest affected posting
-   later posting impact
-   production dependencies
-   closed-period detection
-   preview hash/watermark
-   repair audit creation
-   no execution yet

Acceptance gate:

For every repairable finding, the system can state exactly what must be
reversed/reposted and why.

------------------------------------------------------------------------

## Phase 3 --- Controlled Repair

The repair design is specified. Implement it only after Phases 1 and 2,
and only as sections 4, 11, 12, and 21 describe: multi-type adapter
routing, a fresh concurrency token before every call, `STEP_PREPARED`
before the module operation, and restart reconciliation. Do not start
this phase from an older single-transaction reading of this plan.

Implement only supported strategies:

1.  guided existing rollback/repost orchestration
2.  derived `StockCostState` rebuild where facts are proven correct

Acceptance gate:

No repair mutates immutable facts; all repairs are auditable and
post-repair health is revalidated.

------------------------------------------------------------------------

## Phase 4 --- Period Close Integration

Promote proven critical checks to period-close blockers.

Do this only after Phase 1-3 have been exercised against real data.

------------------------------------------------------------------------

## Phase 5 --- FIFO / Standard Cost diagnostic providers

Only after those costing algorithms are authoritative in V2.

Do not let this UI project become the place where FIFO/Standard
algorithms are secretly implemented.

The costing engine owns valuation; the diagnostic center explains and
validates it.

------------------------------------------------------------------------

# 23. AI coding-agent implementation order

The coding agent should work in this order:

1.  Read all files listed in Section 2.
2.  Verify current `production` HEAD before coding.
3.  Inventory actual `SourceDocumentType`, `MovementCode`,
    `ValuationSource`, and error-code values.
4.  Add contracts and stable diagnostic codes.
5.  Implement read-only diagnostic queries.
6.  Add trace service.
7.  Add production trace adapter.
8.  Add `MenuCodes`, `PermissionCodes.RepairCost` inside `All`,
    `ErpWeb/Menus/menus.xml`, and the explicit ACCESS / VIEW_COST /
    REPAIR_COST seed. Do not edit publish output.
9.  Register services, the Moving Average diagnostic provider, and
    every repair adapter.
10. Implement Costing Center UI using Inventory standard UI.
11. Add tests, including opening-anchor, epoch, and COGS quantity proof.
12. Run Inventory, Procurement, Sales, Production, StockLedger, Period
    Close, Menus/Security, and Costing tests.
13. Only then implement the Repair Planner, Sales ownership edges, and
    per-pool impact rows. Still no execution.
14. Review dependency completeness and repair-adapter coverage.
15. Only then implement the controlled Repair Service as a durable
    saga with `CostingRepairCase` and append-only
    `CostingRepairAuditEvent`.
16. Integrate proven blocking checks into period close last.

The agent must not jump directly to repair buttons.

------------------------------------------------------------------------

# 24. Non-negotiable invariants

1.  `StockValuationFact` remains immutable historical evidence.
2.  Sealed `StockPosting` is never silently rewritten.
3.  Reversal lineage remains explicit.
4.  Tenant/company/branch scope is enforced server-side.
5.  Closed periods are respected.
6.  Posting sequence, not UI date sorting, defines authoritative
    valuation order.
7.  Quantity reconciliation must pass before cost repair is trusted.
8.  Production quantity and value must reconcile together.
9.  FG exact transferred value must remain conserved.
10. A repair cannot be marked successful until diagnostics are rerun.
11. No UI action directly edits a cost field.
12. Every repair requires user, timestamp, reason, before evidence,
    after evidence, and result.
13. Unsupported costing methods are reported explicitly, never silently
    approximated.
14. Diagnostic logic must reuse authoritative engine rules wherever
    possible.
15. The diagnostic center must never become a second costing engine.
16. Production V2 diagnostics must validate immutable
    `ProductionValuationEvidence` rows, not only live pool totals.
17. Pre-cutover/legacy history must be visibly distinguished from
    authoritative V2 coverage.
18. Closed valuation snapshot watermark/hash/revision are hard repair
    boundaries.
19. Repair execution is allowed only through registered
    document-specific authoritative adapters.
20. Unsupported repair chains remain diagnostic/preview-only.
21. Labour/machine/overhead must not be presented as capitalized
    production cost unless the authoritative production posting engine
    actually capitalizes them.
22. Monetary cost visibility uses `VIEW_COST`, never `VIEW_PRICE`.
23. `REPAIR_COST` does not replace the source module POST or ROLLBACK
    permission.
24. DO Force Close is not a costing rollback.
25. Cross-module repair is a saga. A later failure does not pretend
    earlier committed steps were rolled back.
26. Authoritative Moving Average is branch + item + cost method.
    Display filters do not recompute it.
27. A bounded trace starts from an opening anchor, not from zero.
28. Repair impact is reported per item and cost pool.

------------------------------------------------------------------------

# 25. Definition of Done

The feature is production-ready when a normal authorized ERP user can:

1.  Open Costing Diagnostic & Repair Center.
2.  See whether costing is healthy.
3.  Filter to an item/document.
4.  See the chronological cost calculation.
5.  Understand the cost in business language.
6.  Trace the source receipt/issue/production/FG/sale.
7.  See the first point where costing became invalid.
8.  See all affected later documents.
9.  Preview a safe repair.
10. Be prevented from repairing a closed/unsafe dependency chain.
11. Execute an authorized supported repair.
12. See a complete repair audit.
13. Rerun health checks and obtain a clean result.

And a programmer/accountant can independently prove that:

``` text
Active sealed valuation facts
        ↓
StockCostState
        ↓
Inventory valuation
        ↓
Production value transfer
        ↓
Sales COGS / GP
        ↓
Period-close valuation snapshot
```

reconcile under the same authoritative evidence.

------------------------------------------------------------------------

# 26. Final recommendation

**Proceed in phase order.** Phase 1, Phase 2, and Phase 3 are all
specified. Automated repair follows section 12, including fresh
document versions and `STEP_PREPARED` before each module call.

The repository already contains the hard foundations: V2 posting
identity, immutable valuation facts, cost state, exact reversal lineage,
chronology protection, quantity reconciliation, period close, production
pool valuation, exact FG value transfer, and COGS query support.

The highest-value next step is not another costing algorithm. It is to
make those mechanisms **observable and explainable to ERP users**.

Build the Costing Diagnostic Center first as read-only. Prove that it
can find and explain real defects. Then add repair planning, and only
then allow tightly controlled repair execution.

That sequence gives the project a supportable costing system rather than
a powerful but opaque costing engine.

------------------------------------------------------------------------

# 27. Final approval gate

## Score after the fifth-review source check

The fifth-review ownership correction is in this text and matches HEAD
`5b7478b4`. Sales-out identity follows `ResolveSourceIdentity`.
CN-owned customer return follows `SaCdnCrLock`. Purchase-CN vendor
return follows `PoCdn.VrBatchNo`. Goods receipt stays on the
purchase-aware adapter. Adapters register `OwnerTypes`. Repair Mode B
uses sealed, non-reversed facts under the section 8A model. The epoch
choice is still made by those tests, not by guessing. Implement in
phase order.

The plan still distinguishes four separate truths:

1.  **Physical inventory truth** --- `IvBalLoc` + inventory
    reconciliation.
2.  **Financial inventory truth** --- sealed V2 `StockValuationFact` +
    `StockCostState`.
3.  **Production WIP/FG truth** --- immutable production movements,
    allocations, valuation evidence, pool projections, and FG
    exact-value evidence.
4.  **Closed-period truth** --- immutable valuation snapshots with
    epoch, watermark, revision, and source hash.

The implementation must preserve those boundaries.

### Approval condition

Moving Average is the only authoritative V2 engine on the reviewed
HEAD. FIFO and Standard Cost must only be exposed as authoritative
diagnostic providers after their ledger algorithms and evidence models
exist. An earlier "10/10 approved" label in this file is withdrawn
until the post-incorporation review.

### AI agent stop conditions

The coding agent must stop and report rather than improvise when:

-   a required repair path has no authoritative module service/adapter;
-   a dependency cannot be proven from stored identities/evidence;
-   a repair crosses pre-V2 cutover evidence;
-   a repair crosses a closed snapshot boundary that has not
    legitimately been reopened;
-   active ledger epoch changes during the operation;
-   production valuation evidence is incomplete;
-   a proposed fix would require editing/deleting sealed facts or
    immutable production movements;
-   the configured costing method is not implemented by the
    authoritative V2 valuation engine.

Under these conditions, **diagnose and explain; do not auto-fix**.

That constraint is what makes the Costing Diagnostic & Repair Center
safe enough for normal ERP users.

------------------------------------------------------------------------

# 28. Inventory + Procurement final review verdict

## Inventory and Procurement boundary: retained

Sections 2B and 2C remain the procurement accounting boundary. They
are not a whole-system repair approval. On HEAD `5b7478b4`, Purchase
Invoice does not revalue stock.

The most important verified accounting boundary is:

``` text
PO
  ↓
GRN freezes PO-derived provisional inventory cost + GRN-date FX
  ↓
V2 Moving Average inventory valuation
  ↓
later Inventory / Production / Sales consumption

Purchase Invoice
  ↓
matching / InvoicedQty / financial payable evidence
  ↓
does NOT currently perform authoritative stock revaluation
```

Therefore the Diagnostic Center must make the provisional-versus-final
purchase cost difference visible instead of hiding it.

This is a major reason the feature is valuable: a user can see that an
apparent "wrong inventory cost" may actually be a **purchase-price
variance policy gap**, not a corrupted stock ledger.

### Approved implementation boundary

The AI coding agent may implement:

-   Inventory/Procurement health checks CD-029 through CD-038
-   GRN cost evidence explanation
-   PO → GRN → PI matching trace
-   provisional-vs-final price variance inquiry
-   transfer conservation checks
-   adjustment cost-provenance checks
-   vendor-return commercial-vs-valuation explanation
-   Purchase CN ReturnStock dual financial/stock trace
-   Inventory public-service repair adapter
-   safe document-specific procurement repair adapters where existing
    services support them

The agent must **not** implement automatic Purchase Invoice inventory
revaluation as part of this Diagnostic Center project.

That requires a separate approved accounting/costing-engine design
because the correct treatment depends on remaining stock, consumed/sold
stock, Production/WIP, FG, COGS, FX, closed periods, and accounting
variance policy.

With that boundary explicit, the Inventory + Procurement portion is
implementation ready and approved.
