# productionv2 — Open-Period Backdated Stock & Cost Replay
## STRICT FINAL REPOSITORY REVIEW — AI CODING AGENT PLAN v3

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Reviewed CURRENT HEAD:** `a5dead323a0e5b19d7b95b457d1a7bd9c0d98cac`  
**Commit message:** `checkedin  wo trainsfer`  
**Tree:** `2af1c6025c0ac272a7510bd71e8eacc9e810a283`  
**Review scope:** Inventory + Procurement costing + Production + Sales stock/COGS + rollback + period close + concurrency + large-volume performance  
**Plan status:** **FINAL APPROVED FOR IMPLEMENTATION — 10/10, SUBJECT TO ALL HARD ENABLEMENT GATES IN THIS PLAN**
**Supersedes:** v2. v2 must not be implemented after this review.  
**Risk:** CRITICAL. A partial or shortcut implementation can corrupt stock, WIP, FG, FIFO/WAC/Standard valuation and Sales COGS.

---

# 0. FINAL REVIEW VERDICT

This pass re-verified the CURRENT `productionv2` branch at:

```text
HEAD a5dead323a0e5b19d7b95b457d1a7bd9c0d98cac
tree 2af1c6025c0ac272a7510bd71e8eacc9e810a283
```

The branch has not moved since the prior review.

The previous v2 plan substantially improved the original design, but this strict final pass found additional repository-specific issues that are serious enough that **v2 is superseded and must not be implemented**.

The additional findings are:

1. A single `IvTrxHistory` can legitimately own multiple valuation facts:
   - FIFO issues split across several layers;
   - transfers have OUT plus IN facts;
   - future replay generations may change the split count.
   Therefore the system cannot resolve “one active valuation fact”; it must resolve one **complete active valuation generation/set** for each logical history event.

2. Normal `InventoryValuationService.NewFact` and `StockValueAdjustmentWriter` stamp facts from `context.Posting.EffectiveAt`.
   A historical replay can correct many later business events.
   Reusing those writers blindly would stamp corrected later costs into the wrong business period.

3. `StockPostingCoordinator` uses one SQL Server session-context key:
   `STOCK_LEDGER_V2_POSTING_ID`.
   Opening nested root/revaluation posting contexts inside one transaction can overwrite/clear the write context and is unsafe unless explicit context switching/restoration is implemented.

4. Current `CompleteInTransactionAsync` always invokes normal valuation.
   A replay path therefore needs an explicit “replay already valued and reconciled” completion seam; otherwise normal backdate guards would run again or valuation could be duplicated.

5. Purchase Invoice settlement is chronological:
   `BusinessDate -> PostingSequence -> FactId`.
   A late GR can change not only PPV amount but **which GR receipt slice a later PI settles against**.
   Replay must therefore be able to reverse and rebuild receipt-settlement mappings.

6. Current `PurchaseReceiptCostSettlementService.ReverseInvoiceAsync` assumes settlement roots have never already been reversed.
   After historical replay that assumption is false.
   Procurement rollback must resolve the active settlement/adjustment generation, like Inventory valuation does.

7. `StockValuationSnapshotBuilder` checks FIFO “issue” completeness using `Direction < 0` but does not exclude `ValuationStatus = REVERSED`.
   A FIFO receipt reversal is direction `-1` but has no layer-consumption rows.
   Replay introduces more reversal facts, so month-end close must classify active valued FIFO issues correctly before this feature is enabled.

8. `StockPosting` currently lacks an index beginning with:
   `CompanyCode + BranchCode + EffectiveAt`.
   Period-close/replay watermark lookups will otherwise become increasingly expensive.

9. The repository supports cost-method cutover, but cutover is constrained to the first day immediately after a CLOSED prior period.
   Therefore a legitimate replay wholly inside the current OPEN period should never cross a WAC/FIFO/Standard method boundary.
   The replay engine should assert this and block legacy/corrupt exceptions instead of attempting a mixed-method replay.

10. The 15-second `sp_getapplock` value is an **acquisition wait timeout**, not a maximum execution duration.
    Long replay lock holds can make ordinary writers time out.
    Performance certification must therefore control lock-hold duration, not merely “stay under 15 seconds.”

11. Existing inbound physical identity should normally stay immutable when only its cost changes.
    A replay cost delta must be attached to the inbound event’s original chronological position immediately after that receipt and before the next event.

12. A true backdated post cannot be implemented by “run normal post, then recost.”
    The current physical and financial paths reject the transaction before that point.
    The implementation needs an explicit fast-path/replay-path router.

After incorporating these corrections, this v3 plan is **FINAL APPROVED FOR IMPLEMENTATION — 10/10 as a plan**.

That approval does not permit enabling the feature until every hard gate in this document passes.

# 1. LOCKED BUSINESS REQUIREMENT

For every stock-driving transaction:

> **If the transaction period is OPEN, a document may be saved even when its Transaction Date is in the future. It may be POSTED when Transaction Date <= Business Date. A backdated POST inside the OPEN period is allowed even when later transactions already exist. The ERP automatically validates the physical stock timeline and recalculates all affected Inventory, Procurement-cost, Production/WIP/FG and Sales COGS values forward.**

The user must NOT be forced to manually rollback later transactions simply because a legitimate document was entered late.

### Examples

```text
16 Oct action
02 Oct GR +2
October OPEN
=> SAVE  YES
=> POST  YES
=> later costing automatically replayed
```

```text
16 Oct action
20 Oct transaction date
=> SAVE  YES as NEW
=> POST  NO until 20 Oct
```

```text
02 Nov action
15 Oct transaction
October CLOSED
=> POST NO unless October is formally reopened
```

---

# 2. NON-NEGOTIABLE SAFETY RULES

The feature MUST preserve all of the following:

- Transaction Date is business chronology.
- PostedAt/CreatedAt is audit chronology.
- Future Transaction Date may be NEW but cannot POST.
- CLOSED inventory periods are immutable.
- No physical stock slice may become negative at any point in the replayed timeline.
- Stock must actually exist as-of the transaction date before an outbound movement may use it.
- Lot/status/location/warehouse identity remains exact.
- FIFO must consume the correct historical layers.
- Moving Average must be recalculated chronologically.
- Standard Cost must use the revision effective on the movement date.
- Production RM → WIP → FG value must remain fully reconciled.
- Work Order Transfer OUT value must equal IN value.
- Production Stock Return must preserve correct source-cost lineage.
- Sales DO/direct Invoice COGS must reflect the corrected active cost.
- Linked-DO Invoice must never issue stock or COGS twice.
- Sales return/CN stock cost must remain tied to exact active original sale cost.
- Purchase value adjustments/PPV must remain correctly split between inventory and consumed variance.
- Immutable audit evidence remains append-only.
- All derived live states must reconcile before COMMIT.
- Any failure rolls back the whole root operation.

---

# 3. V2 LEDGER IS A HARD PRECONDITION

Automatic open-period historical replay MUST only be enabled for a branch when:

1. one ACTIVE `StockLedgerEpoch` exists;
2. `StockLedgerCompatibility.EnsureCompatibleAsync` passes;
3. V2 stock posting is authoritative for the branch;
4. quantity/valuation reconciliation is clean;
5. no unsealed/orphan posting exists.

Current Inventory has legacy-compatible paths when the coordinator/epoch is unavailable. Those paths do **not** have enough immutable financial evidence for safe automatic recost.

Therefore:

```text
OpenPeriodBackdatedReplayEnabled
    AND Active V2 Ledger
    AND Compatibility PASS
=> replay feature available
```

Otherwise retain the current conservative behavior and do not pretend automatic replay is safe.

Production already enforces V2 on critical stock transactions; Inventory/Sales must follow the same precondition for this new behavior.

---

# 4. DATE-RULE STANDARDIZATION

## 4.1 One authoritative Business Date

All POST-time future-date decisions must use the company business timezone.

Prefer the existing repository authority:

```text
StockBusinessTime.NowAsync(db, companyCode)
```

and compare:

```text
TransactionPostingDateGuard.FutureDateError(
    transactionDate,
    businessNow)
```

or the equivalent shared rule.

Do not introduce server-local `DateTime.Now` as a second Business Date authority.

Audit timestamps remain UTC/current action time.

## 4.2 SAVE

Future-dated NEW drafts are allowed.

Known current Production SAVE blockers that must be changed:

- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`
- `ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs`
- `ErpWeb.Core/Production/ProductionStockReturnService.cs`
- `ErpWeb.Core/Production/ProductionStockAdjustmentService.cs`

Repository-wide search is mandatory for other SAVE-time future-date refusals.

Do not weaken:
- permissions;
- status rules;
- Work Order snapshot rules;
- exact source identity;
- lot expiry as-of the selected future business date;
- other legitimate draft validations.

## 4.3 POST

Every stock-driving POST must enforce:

```text
TransactionDate.Date <= BusinessDate.Date
```

Known critical gap:

`ErpWeb.Core/Production/ProductionOutputService.Posting.cs`

currently creates its V2 posting from `output.ProductionDate` without an explicit common future Business Date guard.

Also verify:
- Inventory MR/GR/MI/Scrap/VR/TR/ADJ;
- DO;
- direct Invoice shipment;
- CDN stock return;
- IP;
- Daily Production;
- PRTN;
- PADJ;
- FG;
- WO Transfer;
- Procurement zero-quantity costing posts.

## 4.4 CLOSED period

A replay may only alter the business chronology of an OPEN period.

A transaction in a CLOSED period must fail unless the existing formal reopen process is used.

Never “correct” a closed month by stamping the replay into today while pretending the old business event changed.

## 4.5 ROLLBACK audit vs business chronology

For replay-aware rollback:

```text
business removal position = original business event position
RollbackDate / PostedAtUtc = actual action time
```

The audit says when the user acted.
The replay says which historical business event was removed.

This rule is mandatory for open-period historical correction.

Existing legacy/current-date reversal evidence must remain readable; do not rewrite old rows.

# 5. DO NOT USE THE PERIOD-CLOSE SNAPSHOT BUILDER AS THE POSTING REPLAY ENGINE

`IvPeriodCloseService.Snapshot.cs` currently performs branch-wide snapshot work.

It is useful as:
- a proven stock-slice definition;
- a reconciliation oracle;
- month-end validation.

It MUST NOT be called on every transaction post.

That would eventually scan huge history and hold the branch stock lock too long.

The posting replay engine must use **targeted bounded tail queries**.

---

# 6. CORE DESIGN: BOUNDED TAIL REPLAY

This is the core performance rule.

Never replay from the beginning of company history.

For a candidate event at `T`:

1. read current affected state;
2. load only affected active events after `T`;
3. rewind that bounded tail in memory;
4. apply the candidate;
5. replay the same tail forward;
6. calculate changed effective costs/dependencies;
7. persist append-only corrections plus final derived state;
8. reconcile before COMMIT.

Complexity must scale with:

```text
affected stock slices
+ affected item cost events
+ affected Production descendants
+ affected Procurement/Sales allocation descendants
```

not with company lifetime history.

## 6.1 Fast path vs replay path

Do **not** remove the existing chronology guards globally.

Add one central decision seam such as:

```text
IStockChronologyExecutionRouter
```

Decision:

```text
no later active physical/financial dependency
=> existing proven chronological fast path

later active dependency
AND feature OFF
=> current conservative blocker

later active dependency
AND feature ON
AND V2 replay readiness PASS
=> replay path
```

This keeps the ordinary current-date transaction path small, proven and fast.

The replay path is exceptional and must be entered deliberately.

## 6.2 Route before the normal blockers finish the POST

The implementation cannot:

```text
normal POST
then recost
```

because today:
- physical Inventory cores reject later movements;
- valuation rejects later facts.

The router must intercept before those blockers return failure and transfer control into the replay orchestrator while preserving every other validation.

No caller may pass a generic `skipChronologyChecks=true` flag.

Use a strongly scoped internal replay execution context that can only be created by the replay orchestrator.

# 7. ACTIVE LOGICAL MOVEMENT / VALUATION MODEL

Create one shared lineage model. Do not let Inventory, Production, Procurement and Sales independently guess which rows are current.

Required concepts:

```text
IActivePhysicalMovementResolver
IActiveValuationGenerationResolver
IActiveValueAdjustmentResolver
IActivePurchaseSettlementResolver
IActivePurchaseCostAdjustmentResolver
IActiveProductionCostResolver
IActiveSalesReturnAllocationResolver
```

## 7.1 Physical movement activity

V2/Production history is append-only.

A forward event cancelled by its linked reversal is no longer an active business event.

This is especially important for Production because some existing rollback movements use the rollback action time; simply netting by movement date can put the cancellation in the wrong historical position.

The resolver must collapse:
- original;
- reversal;
- any permitted correction lineage

into the current active business movement set.

## 7.2 Valuation generation is a SET, not one fact

A single `IvTrxHistory` may have:

### Moving Average transfer

```text
split 0 = OUT
split 1 = IN
```

### FIFO issue

```text
split 0 = FIFO layer A
split 1 = FIFO layer B
...
```

### FIFO transfer

```text
issue splits 0..n-1
receipt split n
```

Therefore:

```text
ActiveValuationGeneration
    InventoryHistoryId
    GenerationStockPostingId
    Facts[]
```

The resolver must:

1. find business-direction/value facts that have no linked reversal child;
2. group the surviving facts by generation `StockPostingId`;
3. require the complete active set to belong to exactly one generation;
4. verify expected OUT/IN/FIFO split completeness;
5. fail reconciliation if two competing active generations exist.

A replay must reverse **all facts in the old active generation** before appending the replacement generation.

For FIFO, the replacement generation may legitimately contain a different number of splits.

## 7.3 Reversal facts are not active issue facts

A reversal fact itself may be a leaf in the reversal chain but it is not the active business issue generation.

Do not implement “active” as merely:

```text
no child ReversesValuationFactId
```

without also considering:
- `ValuationStatus`;
- logical business direction;
- source history/event;
- generation grouping.

## 7.4 Value-only facts

Zero-quantity PPV/Standard/revaluation facts may not have an `InventoryHistoryId`.

Resolve those by immutable value-adjustment lineage:

```text
OriginalValuationFactId
ReversesValuationFactId
SourceDocument identity
```

and require one active logical generation.

## 7.5 Active purchase settlement generation

A PI settlement can become:

```text
old settlement root
 -> reversal
new replacement settlement root against a different GR
```

The active resolver must collapse settlement and PurchaseCostAdjustment chains and return the current allocation only.

Later PI rollback/CN logic must operate on that current active allocation, not assume every original root is unreversed.

# 8. PHYSICAL STOCK TAIL REPLAY

## 8.1 Reuse the existing as-of architecture

The repository already contains:

`ErpWeb.Core/Inventory/InventoryAsOfStockService.cs`

which reconstructs past stock by:

```text
current balance
- later inbound
+ later outbound
```

This is the correct architectural direction.

Create a strict replay variant/core from this concept.

Do NOT use its inquiry clamping:

```text
Math.Max(..., 0)
```

during posting validation.

A posting validator must detect negative chronology; it must never hide a negative as zero.

## 8.2 Full stock-slice identity

Physical replay must remain scoped to:

```text
Company
Branch
Item
Warehouse
Location
Lot
Status
```

and exact `IvBalLocId` where already established.

## 8.3 Strict algorithm

For each affected balance:

### Step A — current state

Read locked/current:

```text
IvBalLoc.StdQty
```

### Step B — reconstruct candidate-time state

Load active movements after candidate timestamp.

Reverse them newest → oldest:

```text
later inbound  => subtract
later outbound => add
```

This reconstructs quantity immediately before candidate in the existing timeline.

### Step C — apply candidate

Apply candidate in/out legs.

Reject if source quantity is insufficient.

### Step D — replay tail forward

Replay oldest → newest.

At every event:

```text
running qty >= 0
```

must hold.

If not, reject the root operation and name the first offending document.

### Step E — current-state check

Final replayed quantity must equal:

```text
old current qty + candidate net physical delta
```

for a new post, or the corresponding inverse for rollback.

Only then update `IvBalLoc`.

## 8.4 Dual availability condition

For outbound posting both must be true:

1. enough stock exists **as-of the candidate date**;
2. enough current stock remains for the physical change.

This prevents both:
- using future stock historically;
- consuming stock that is no longer physically present today.

---

# 9. `IvBalLoc.TransDate` — CHANGE ITS ROLE

New invariant:

> `IvBalLoc.TransDate` is the latest active physical movement date for that balance identity.

A late 02-Oct transaction posted after a 12-Oct movement must NOT move `TransDate` backward to 02-Oct.

Normal post:

```text
TransDate = max(existing latest active date, candidate date)
```

Rollback/replay:

```text
TransDate = latest remaining active physical movement date
```

if none remain, use the repository's opening-balance rules.

## 9.1 `TransDate` is NOT historical availability authority anymore

Current code that filters old-date stock solely by:

```text
IvBalLoc.TransDate <= documentDate
```

becomes wrong once TransDate means “latest active movement.”

Mandatory review/fix includes:

- `IvInventoryPostingService.ValidateBalanceStockDate`
- Inventory transaction source selection
- `IvStockCommonRepository.SearchOnHandPagedAsync(asOfDate)`
- stock-count candidate as-of usage
- `IvSpShipmentService.DiscoverFifoPilesAsync`
- DO/Invoice shipment lot selection
- other `IvBalLoc.TransDate` as-of filters.

Production Material Issue already uses `InventoryAsOfStockService`; use that pattern.

Keep TransDate as a fast path only when:

```text
candidate date >= latest TransDate
```

For a true backdate, strict as-of replay is authoritative.

---

# 10. SAME-TIMESTAMP ORDERING

Never invent a clock time the user did not enter.

Base stock/cost order:

```text
1. business EffectiveAt / Transaction datetime
2. persisted StockPosting.PostingSequence
3. logical posting line / stable identity
```

For a new candidate whose EffectiveAt exactly equals existing events:

```text
existing persisted same-time events keep their relative order
new candidate is appended deterministically after them
```

unless the source document genuinely stores a more precise trusted business timestamp.

## 10.1 Attached restatement sub-order

A cost restatement that belongs to an existing inbound event is not a free-standing later event.

It must execute logically:

```text
physical inbound event
then its cost restatement delta
then the next chronological business event
```

This is essential for WAC and FIFO.

Do not let a receipt cost correction drift to the end of that date merely because the correction was posted today.

## 10.2 Preserve module-specific proven ordering

Where the current repository has a more specific deterministic business allocator, reuse it.

Example:

`PurchaseReceiptCostSettlementService`

orders GR candidates by:

```text
BusinessDate
PostingSequence
FactId
```

A replayed PI settlement must use that exact comparator.

Do not invent a new receipt settlement order.

# 11. FINANCIAL TAIL REPLAY — COMMON MODEL

Financial pool remains:

```text
Company + Branch + Item + CostMethod
```

For each affected item:

1. capture current `StockCostState`;
2. load the bounded active financial tail after candidate position;
3. rewind it newest -> oldest;
4. apply candidate;
5. replay oldest -> newest;
6. restate affected outbound/value/receipt-cost consequences;
7. update derived current state;
8. reconcile.

Financial events include:
- receipt;
- issue;
- transfer;
- customer return;
- Production FG/return stock-in;
- PPV / purchase value adjustment;
- Standard revaluation;
- other zero-quantity financial value events.

## 11.1 Cost-method boundary invariant

`StockCostMethodCutoverService` currently permits cutover only:

```text
first day of an OPEN period
AND immediately after the prior period is CLOSED
```

Therefore a valid candidate inside the current OPEN period should not have a cost-method cutover between the candidate and its replay tail.

Add a hard preflight:

```text
if a cost-method cutover/policy boundary exists inside replay range
=> BLOCK
=> COST_METHOD_BOUNDARY_IN_REPLAY_RANGE
```

Treat it as legacy/corrupt/unsupported state requiring investigation.

Do not attempt to run WAC then FIFO then Standard in one online replay.

Standard-cost **item revisions** are different: they may be resolved per event date and must still be replayed normally.

# 12. IMMUTABLE FINANCIAL EVIDENCE

Never UPDATE or DELETE sealed valuation facts.

## 12.1 One root stock-posting write context

Preferred repository-safe design:

> The triggering business document's unsealed V2 `StockPosting` remains the single SQL Server stock-write context for the whole atomic replay transaction.

Reason:

`StockPostingCoordinator` sets one connection/session key:

```text
STOCK_LEDGER_V2_POSTING_ID
```

and the database write guard requires that unsealed posting for:
- `IvBalLoc`;
- `IvTrxHistory`;
- `PrProductionBalLot`;
- `PrProductionBalLotMovement`;
- `PrMaterialMovement`;
- `PrProductionMovementAllocation`.

Do not open nested StockPosting contexts and rely on the coordinator to restore the previous session value; it currently clears the value on completion.

If an implementation chooses separate revaluation StockPostings, it must first implement explicit push/restore context semantics and prove them in SQL Server failure/concurrency tests. Otherwise it is NOT approved.

## 12.2 Add replay audit metadata

Add a small append-only replay header such as:

```text
StockReplayRun
--------------
Id
CompanyCode
BranchCode
RootStockPostingId
RootRequestId
RootDocumentType
RootDocumentId
EarliestEffectiveAt
LatestEffectiveAt
PlanWatermark
PlanHash
AffectedHistoryCount
AffectedItemCount
AffectedProductionMovementCount
CreatedAtUtc
CreatedBy
```

`RootStockPostingId` unique for one replay execution.

Do not place the entire dependency graph in `SourceSnapshotJson`.
Store hashes/counts plus normalized evidence.

## 12.3 Correction facts may preserve another document's owner identity

A corrected later fact can be physically stored under the root replay posting while preserving:

```text
SourceDocumentType
SourceDocumentId
SourceDocumentNo
SourceDocumentLine
InventoryHistoryId
OriginalValuationFactId
```

as the actual business owner/lineage.

Audit UIs must distinguish:
- trigger posting;
- business owner;
- original fact lineage.

## 12.4 Replay posting-line allocator

`UQ_StockValuationFact_PostingLineSplit` is unique on:

```text
StockPostingId + PostingLineNo + SplitOrdinal
```

Many corrected histories have original line `1`.

Therefore the replay writer must allocate unique posting-line numbers inside the root posting.

Concept:

```text
root physical history uses its normal posting lines
replay correction lines start after max root line
one replay PostingLineNo per corrected logical history/value event
FIFO splits use SplitOrdinal 0..n
transfer IN follows issue splits deterministically
```

Do not reuse source posting-line numbers directly.

## 12.5 Event-level EffectiveAt is mandatory

Normal:

```text
InventoryValuationService.NewFact
StockValueAdjustmentWriter
```

stamp:

```text
EffectiveAt = context.Posting.EffectiveAt
BusinessDate = context.Posting.BusinessDate
PeriodKey = context.Posting.PeriodKey
```

That is correct for normal posting but **not** sufficient for multi-event replay.

The replay fact writer must explicitly stamp each correction with the original event position being corrected:

```text
fact.EffectiveAt = correctedEvent.EffectiveAt
fact.BusinessDate = correctedEvent.EffectiveAt.Date
fact.PeriodKey = yyyy-MM of correctedEvent.EffectiveAt
```

The trigger posting's `PostedAtUtc` remains the actual correction action time.

This is required so month-end snapshots place reversal/replacement/value deltas in the correct period.

Do not call normal `NewFact`/`StockValueAdjustmentWriter` blindly for replay facts unless those APIs are extended to accept and validate an explicit event EffectiveAt.

## 12.6 Existing outbound valuation changes

For an outbound generation whose cost/allocation changes:

```text
old active generation facts
 -> append exact reversal facts
 -> append complete corrected generation facts
```

Reverse every old split.

For FIFO also reverse the matching old consumption rows and append corrected consumption rows.

## 12.7 Existing inbound valuation changes

When physical receipt identity remains valid, do not manufacture a duplicate receipt.

Prefer:

```text
immutable original physical receipt fact
+ append-only value restatement delta attached to that receipt event
```

Then:
- WAC applies the delta immediately after receipt;
- FIFO adjusts the receipt layer's accumulated/remaining value and propagates consumed effect;
- Standard normally updates variance rather than inventory actual value.

## 12.8 Replay-aware completion seam

Current `StockPostingCoordinator.CompleteInTransactionAsync` always calls normal valuation.

Add a narrowly scoped internal completion operation, for example:

```text
CompleteReplayInTransactionAsync(...)
```

It may seal only after verifying:

- every newly added V2 `IvTrxHistory` requiring value has a complete active valuation generation;
- no pending/unvalued required history remains;
- `StockCostState` reconciles;
- FIFO reconciles;
- Production reconciles;
- Procurement/Sales allocation reconciles;
- replay header/plan hash is persisted;
- root posting is still unsealed;
- SQL session context still references the root posting.

It must NOT simply expose `skipValuation=true`.

The normal completion API remains unchanged for ordinary posts.

# 13. IMPORTANT FIFO REFINEMENT — DO NOT REPLACE EXISTING RECEIPT IDENTITY JUST BECAUSE ITS COST CHANGED

If an existing physical receipt remains valid but its effective cost changes, preserve:

- `IvTrxHistory`;
- original receipt valuation identity;
- lot identity;
- purchase settlement receipt identity where applicable;
- FIFO origin layer identity.

Examples:
- FG receipt whose upstream WIP cost changed;
- customer return whose original COGS changed;
- Production Stock Return whose WIP cost changed.

Use an append-only receipt cost restatement/value delta.

## 13.1 WAC

At the receipt's original event position:

```text
apply original receipt qty/value
apply restatement delta
then continue replay
```

## 13.2 FIFO

Do not create another physical layer.

Apply corrected value through:

```text
StockFifoLayer.AccumulatedAdjustment
StockFifoLayer.RemainingValue
CurrentUnitCost
```

for the still-unconsumed portion.

The portion already consumed must flow into corrected downstream issue valuation/COGS through issue-generation replay.

Invariant:

```text
corrected original receipt total value
=
remaining corrected layer value
+ corrected value already consumed downstream
```

## 13.3 Customer return FIFO layer

`AppendFifoCustomerReturnAsync` currently creates a real receipt layer.

If the return's exact original-sale cost later changes:
- preserve that layer identity;
- restate its value;
- replay any subsequent consumption of that returned stock;
- restate `SalesReturnCostAllocation`.

## 13.4 Do not change `OriginalValue` casually

The repository already uses `AccumulatedAdjustment` for FIFO value adjustments.

Prefer the same pattern rather than rewriting immutable/historical meaning into `OriginalValue`.

Only change the live derived remainder fields as part of atomic replay.

# 14. MOVING AVERAGE REPLAY

Reverse tail using known active signed qty/value:

```text
reverse later receipt     => subtract qty/value
reverse later issue       => add qty/value
reverse value adjustment  => subtract signed value
```

Then replay forward:

### Receipt

Use frozen/authoritative receipt cost evidence.

### Issue

Use the moving average immediately before the issue.

### Value adjustment

Apply at its true EffectiveAt.

At the end:

```text
StockCostState.OnHandBaseQty
StockCostState.InventoryValue
StockCostState.CurrentUnitCost
StockCostState.AverageUnitCost
```

must equal the sum/effect of active valuation evidence.

A zero quantity state must not retain unexplained residual inventory value.

---

# 15. FIFO REPLAY

## 15.1 In-memory rewind

Clone affected current FIFO layer state.

Reverse affected later events newest → oldest.

### Later issue

Use the currently active issue facts and `StockFifoLayerConsumption` to restore:

```text
RemainingQty
RemainingValue
OPEN/CLOSED
```

### Later receipt

Only after all its later consumptions are rewound may its receipt contribution be logically removed from the reconstructed state.

### Later value adjustment

Reverse its layer/value effect.

Do not mutate immutable historical consumption evidence during planning.

## 15.2 Forward replay

Reapply:

- candidate receipt/issue/value adjustment;
- later receipts;
- later issues;
- later PPV/value adjustments

in chronological order.

FIFO allocation order remains:

```text
ReceiptEffectiveAt
Origin valuation identity
layer ID / deterministic tie
```

## 15.3 Persist corrections

For an outbound allocation that changed:

- append valuation reversal for old active issue fact;
- append corrected issue fact;
- append `StockFifoLayerConsumption` reversal rows for old allocation;
- append corrected consumption rows.

Existing receipt layers remain the physical origin authority where quantity identity did not change.

Mutable derived layer fields are then set to the newly calculated current state.

## 15.4 Final FIFO gates

Must prove:

```text
sum OPEN layer RemainingQty = StockCostState.OnHandBaseQty
sum OPEN layer RemainingValue = StockCostState.InventoryValue
no RemainingQty < 0
no RemainingValue < 0
every active FIFO issue has complete consumption evidence
every consumption total equals issue fact qty/value
```

---

# 16. STANDARD COST REPLAY

Standard inventory movements use the approved Standard Cost revision effective at each business date.

Do not substitute current/today's revision.

Replay must include:

- Standard cost revisions
- Standard revaluation value-only postings
- Production Standard variance
- Sales Return Standard variance.

Manual/frozen authority remains frozen where the existing business contract says it is approved evidence.

---

# 17. PROCUREMENT COST SETTLEMENT / PPV IS MANDATORY SCOPE

This is mandatory scope.

Current repository includes:
- `PurchaseReceiptCostSettlementService`;
- `PurchaseCostAdjustmentPostingService`;
- `PurchaseCdnCostAdjustmentService`;
- PI/CN V2 costing postings.

## 17.1 Late GR can change receipt settlement identity

Current PI allocation orders matching GR valuation facts by:

```text
BusinessDate
PostingSequence
FactId
```

and settles PI quantity across those candidates.

Therefore a late GR can change:

```text
which ReceiptValuationFactId
which ReceiptInventoryHistoryId
which receipt batch
```

a later posted PI should be mapped to.

Replay cannot merely recalculate PPV amount while leaving stale settlement mapping.

## 17.2 Procurement replay algorithm

For each affected PO/release/line/item and later posted PI:

1. reconstruct active receipt candidates using the exact existing allocator order;
2. reconstruct active prior settlement quantities;
3. recompute the PI line settlement mapping;
4. compare current active settlement vs corrected mapping;
5. if changed:
   - append settlement reversal rows for the superseded active allocation;
   - append replacement settlement roots for the corrected GR allocation;
   - reverse superseded PurchaseCostAdjustment generation;
   - append corrected PurchaseCostAdjustment generation;
   - replay resulting inventory value/consumed variance.

Do not mutate old settlement rows.

## 17.3 Active settlement/adjustment resolver is required

Current `ReverseInvoiceAsync` loads settlement roots with:

```text
ReversesSettlementId == null
```

and currently refuses rollback when any root already has a reversal child.

That assumption becomes invalid after historical replay.

Refactor PI rollback so it resolves the **current active settlement generation** and reverses that active generation only.

Do the same for active `PurchaseCostAdjustment`.

Purchase CN logic must also use the active generation.

## 17.4 FIFO PI variance

FIFO purchase variance currently splits:

```text
TotalAdjustmentAmount
=
InventoryAdjustmentAmount
+
ConsumedVarianceAmount
```

based on open quantity/value remaining on the exact receipt layer.

Historical replay can change both:
- which GR the PI settles against;
- how much of that GR is still open at PI date.

Therefore recompute both allocation identity and split.

## 17.5 Commercial facts remain commercial

Do not alter:
- supplier invoice gross/net/tax;
- PO commercial amount;
- currency rate;
- approved commercial source values

unless the business document itself is being edited/reposted.

Replay only corrects stock-cost settlement/valuation lineage.

## 17.6 Purchase CN / rollback

Purchase CN must resolve the current active PI settlement/adjustment leaves.

A CN/rollback must not fail merely because an earlier replay legitimately reversed the original root and created a corrected active replacement.

Add regression tests for:

```text
GR -> PI
late earlier GR -> settlement replay
then PI rollback

GR -> PI -> CN
late earlier GR -> settlement replay
then CN rollback
```

# 18. PRODUCTION EFFECTIVE COST ARCHITECTURE

Inventory replay alone is insufficient.

Current Production uses immutable audit movements plus mutable live pools.

Keep immutable:

- `ProductionBalLotMovement`
- `ProductionMaterialMovement`
- `ProductionFinishedGoodFact`
- `ProductionStockReturnFact`
- `ProductionWorkOrderTransferFact`
- `ProductionValuationEvidence`
- dependency/allocation evidence.

Do not rewrite posted historical cost fields in-place.

## 18.1 Add an append-only effective-cost restatement layer

If no existing entity can represent this exactly, add:

```text
ProductionCostRestatementFact
--------------------------------
Id
CompanyCode
BranchCode
ReplayRequestId
ReplayStockPostingId
ProductionBalLotMovementId
EffectiveAt
PriorEffectiveTotalCost
CorrectedEffectiveTotalCost
CorrectedUnitCost
DeltaCost
ReversesRestatementFactId nullable
CreatedAtUtc
CreatedBy
```

Primary logical target is `ProductionBalLotMovementId`.

Do not duplicate the same correction separately on every material/fact table.

Create:

```text
IProductionEffectiveCostResolver
```

which resolves in batch:

```text
active restatement corrected cost
OR
immutable original movement TotalCost
```

No N+1 lookup.

If implementation proves an existing repository mechanism provides identical append-only revision semantics, reuse it and document the proof before omitting this table.

## 18.2 Immutability guard

New restatement rows are append-only.

Add AppDbContext protection.

Also add a test proving sealed Production movements cannot be silently changed after posting outside their legitimate pre-seal synchronization path.

---

# 19. PRODUCTION TAIL REPLAY

For every affected Production pool:

1. read current `ProductionBalLot` qty/value;
2. load only affected active movements later than replay boundary;
3. reverse tail newest → oldest using **effective**, not raw original, movement cost;
4. apply candidate/upstream cost change;
5. replay forward;
6. recompute movement effective costs;
7. append restatements only when effective cost changed;
8. write final live pool qty/value.

Final live state:

```text
ProductionBalLot.BaseQty
ProductionBalLot.Qty
ProductionBalLot.TotalCost
ProductionBalLot.AverageUnitCost
ProductionPoolValuation.TrackedBaseQty
ProductionPoolValuation.TrackedValue
```

must reconcile.

`LastMovementDate` and `LastStockEventEffectiveAt` must remain latest active business movement dates.

---

# 20. PRODUCTION QUANTITY ALLOCATION / DEPENDENCY REPLAY

A backdated **physical Production movement**, not merely a cost correction, may change which receipt contribution a later consume/return uses.

When required:

- reverse superseded `ProductionMovementAllocation` rows;
- append corrected allocation rows;
- reverse superseded `ProductionPoolDependency` edges;
- append corrected dependencies.

Never modify/delete immutable rows.

Existing descendant traversal must be cycle-safe.

If a dependency cycle is detected:

```text
BLOCK + rollback entire root transaction
```

with diagnostic IDs.

---

# 21. IP / MATERIAL ISSUE

Inventory valuation of IP may change under WAC/FIFO replay.

Preserve:

- issue qty
- Work Order material
- operation
- warehouse/location/lot/status
- posted document status.

Correct effective cost must flow to the Production issue contribution.

Current `InventoryValuationService.SynchronizeProductionMaterialCostAsync` mutates the material movement during original pre-seal posting. Replay must not mutate that sealed row later; use the effective-cost restatement layer.

Replacement Inventory valuation facts must preserve the Production identity references.

---

# 22. DAILY PRODUCTION / WIP

Daily Production:

- consumes Production pool/WIP cost;
- adds frozen conversion costs;
- produces downstream WIP/FG staging.

On replay:

```text
Corrected output effective cost
=
corrected consumed material/WIP cost
+
original frozen conversion cost
```

Do NOT recalculate unrelated approved/frozen:
- labour fact
- machine fact
- overhead/conversion fact

merely because raw-material cost changed.

Physical produced/consumed quantity changes only if the root physical transaction itself changes quantity.

Also add the missing future-post Business Date guard.

---

# 23. PRODUCTION STOCK RETURN

Current code values return from verified live pool cost and exact original issue identity.

Replay rules:

- preserve source issue lineage;
- use corrected effective source contribution cost;
- restate Inventory return value where needed;
- do not value with today's arbitrary current cost;
- preserve exact returned warehouse/location/lot/status.

Current rollback blocks later Production/Inventory movements and restores old operational price snapshots.

Replay-enabled rollback must instead:

- remove the selected business movement from chronology;
- replay later Production/Inventory tails;
- recompute final operational caches;
- not restore stale old price cache over legitimate later movements.

Price snapshots stay immutable audit evidence.

---

# 24. PRODUCTION STOCK ADJUSTMENT

Preserve current authority semantics exactly.

### CURRENT_PRODUCTION_POOL_COST

Replay from corrected pool cost as-of adjustment date.

### ORIGINAL_ISSUE_COST

Resolve corrected effective original issue cost.

### STANDARD_COST

Use approved Standard revision effective on adjustment date.

### MANUAL_APPROVED

Frozen authority. Never auto-change.

### ZERO_COST_APPROVED

Frozen zero-cost authority. Never auto-change.

PADJ must continue not to manufacture Inventory history/valuation when the current contract says it is Production-only.

---

# 25. WORK ORDER TRANSFER

Current transfer enforces exact OUT/IN identity and value.

Replay invariant:

```text
effective Transfer OUT total value
==
effective Transfer IN total value
```

If source WIP effective cost changes:

- restate source OUT;
- restate target IN atomically;
- propagate target pool descendants.

Never allow one side to change independently.

Preserve quantity/UOM/lot/route/WO identity.

---

# 26. FINISHED GOOD RECEIPT

FG quantity remains unchanged for cost-only replay.

If source WIP cost changes:

1. recalculate FG effective production value;
2. append Production cost restatement;
3. correct Inventory FG financial value without inventing a second physical receipt;
4. for FIFO, adjust existing receipt cost basis and replay later consumptions;
5. update Production Standard variance when applicable;
6. propagate to later DO/Invoice COGS.

Current physical `ProductionFinishedGoodFact` and lot-origin evidence remain immutable.

---

# 27. SALES COST PROPAGATION

## 27.1 DO

DO shipment is stock/COGS owner for its physical shipment.

Correct later valuation while retaining:

```text
SourceDocumentType = SA_DO
SourceDocumentNo
SourceDocumentLine
```

Sales amount is unchanged.

## 27.2 Direct Invoice shipment

For `LinkDo == false`, Invoice owns physical shipment/COGS.

Preserve:

```text
SA_INVOICE
```

owner identity.

## 27.3 Invoice linked to DO

Keep current repository invariant:

```text
LinkDo == true
=> no second SP shipment
=> no duplicate stock
=> no duplicate COGS
```

Invoice Gross Profit reads corrected active DO facts.

## 27.4 Sales CN/CDN

### ReturnStock = false

No physical replay.

Do not change customer/tax/commercial amount due to recost.

### ReturnStock = true

Customer Return must be based on exact active original outbound valuation.

If original outbound COGS is replayed:

- reverse superseded active `SalesReturnCostAllocation`;
- append corrected allocation against the current active outbound fact;
- correct return stock value;
- for Standard, correct `SalesReturnStandardCostVariance`.

Never lose the exact returned-quantity-to-original-COGS lineage.

---

# 28. SALES / REPORTING ACTIVE-FACT SEMANTICS

Current:

- `StockValuationQueryService`
- `SalesGrossProfitInquiryService`

already use:

```text
ValuationStatus == VALUED
AND no child ReversesValuationFactId
```

Keep and centralize that semantic.

After replay all COGS/Gross Profit queries must use the same active-generation resolver.

Repository-wide audit must find every query reading:

```text
StockValuationFact.CostAmount
ProductionBalLotMovement.TotalCost
ProductionMaterialMovement.TotalCost
ProductionFinishedGoodFact.TotalValue
ProductionStockReturnFact.TotalValue
ProductionWorkOrderTransferFact.TotalValue
```

Classify each reader as:

- immutable audit display, OR
- current/effective costing display.

Current/effective reports MUST use effective restated cost.

Audit screens may expose both:

```text
Original Posted Cost
Current Effective Cost
```

when useful.

No hidden mixed semantics.

---

# 29. ROLLBACK — ACTIVE GENERATION IS CRITICAL

Rollback must operate on the current business/valuation generation, not the originally posted cost generation.

## 29.1 Inventory valuation rollback

Current:

```text
AppendExactReversalAsync
AppendFifoExactReversalAsync
```

load every fact for `InventoryHistoryId` and assume one original generation.

That is invalid after replay.

Refactor to:

```text
activeGeneration = ActiveValuationGenerationResolver.Resolve(historyId)
```

The returned generation is a **complete fact set**, possibly:
- one WAC issue;
- OUT+IN transfer;
- several FIFO issue splits;
- FIFO issue splits + transfer IN.

Then reverse every active fact/split exactly once.

For FIFO, reverse the active generation's consumption rows only.

## 29.2 Procurement rollback

PI/CN rollback resolves current active:
- settlement leaves;
- PurchaseCostAdjustment leaves;
- active inventory value facts.

Do not fail merely because the original settlement root has already been superseded by replay.

## 29.3 Sales return rollback

Use current active SalesReturnCostAllocation chain and active outbound COGS generation.

## 29.4 Production rollback

Use current effective Production movement cost/restatement generation.

Do not restore stale old `UnitPrice/Cost/PriceEvidence` snapshots over later legitimate activity.

## 29.5 Historical business removal flow

For an old OPEN-period POSTED document:

1. validate permission/status/open period;
2. resolve its active physical/financial generation;
3. build removal candidate at original business position;
4. strict physical replay without it;
5. financial replay;
6. Procurement settlement replay where applicable;
7. Production dependency replay;
8. Sales return/COGS replay;
9. move the root document to NEW according to existing lifecycle;
10. leave later unrelated operational documents POSTED;
11. reconcile;
12. COMMIT atomically.

If removing the event makes a later physical document impossible:

```text
BLOCK
```

with the first failing later document.

# 30. OPERATIONAL PRICE / COST CACHE SYNC

Financial authority is V2 ledger evidence, not `IvBalLoc.UnitPrice/Cost`.

Historical replay must not restore stale old cache snapshots over later legitimate changes.

Create one final derived-state synchronizer:

```text
InventoryOperationalCostCacheSync
```

After successful financial replay it may update operational/display caches from current authoritative state according to current repository policy.

Likewise Production final derived state must synchronize:

```text
ProductionBalLot.TotalCost
ProductionBalLot.AverageUnitCost
ProductionPoolValuation
```

Immutable original snapshots remain untouched for audit.

---

# 31. TWO-PHASE CONCURRENCY DESIGN — MINIMIZE BRANCH LOCK TIME

Current `BranchStockTransactionLock` uses transaction-owned SQL Server `sp_getapplock` per Company+Branch.

The lock serializes stock writers.

## Phase A — read-only impact planning outside the branch write lock

Build a deterministic immutable replay plan using AsNoTracking.

Capture:

```text
StockPostingBranchSequence.LastSequence
root document RowVersion
affected IvBalLoc RowVersions where read
affected ProductionBalLot RowVersions where read
Work Order/snapshot hashes where relevant
active V2 epoch id/version
cost-policy revision identity
PlanHash
```

Discover:
- physical slices;
- items;
- active financial tail;
- FIFO layers/consumptions;
- purchase settlements/adjustments;
- Production descendants;
- Sales COGS/return allocations.

Use immutable sealed stock evidence instead of re-reading mutable posted business documents wherever possible.

## Phase B — atomic locked execution

1. begin SQL transaction;
2. acquire branch stock lock;
3. verify ACTIVE epoch/compatibility;
4. re-read branch sequence watermark;
5. re-read root RowVersion;
6. re-read affected live-state RowVersions/snapshot hashes;
7. if stale:
   - rollback;
   - discard plan;
   - bounded re-plan retry;
8. create/begin the root V2 posting context;
9. apply candidate physical change;
10. execute bounded replay under the same root write context;
11. persist correction evidence;
12. run all reconciliation gates;
13. `CompleteReplayInTransactionAsync`;
14. commit.

## 31.1 Bounded retry only

Do not spin indefinitely under write contention.

Use a small bounded automatic re-plan count.

After that return a retryable:

```text
STOCK_BUSY / REPLAY_PLAN_STALE
```

and leave all data unchanged.

## 31.2 Session-context invariant

At every write to guarded stock/Production tables:

```text
SESSION_CONTEXT('STOCK_LEDGER_V2_POSTING_ID')
==
root unsealed StockPosting.Id
```

Test this with SQL Server failure injection.

## 31.3 Lock timeout meaning

`BranchStockTransactionLock.DefaultTimeoutMs = 15000` is how long a competing writer waits to ACQUIRE the lock.

It is not a maximum lock-hold duration.

Therefore online replay certification must keep normal replay lock hold sufficiently below the waiter timeout envelope so ordinary users do not receive avoidable `StockBusy`.

Do not “fix” replay by increasing the timeout.

# 32. NO N+1 REPLAY

All replay engines must batch-load.

Forbidden inside large movement loops:

```text
await db....SingleAsync(...)
await db....AnyAsync(...)
await db....ToListAsync(...)
```

per movement/fact.

Load required rows in sets, then use dictionaries/lookups in memory.

Examples:

```text
factsByHistory
factsByItem
movementsByPool
dependenciesByContributor
allocationsByReceipt
returnsByOriginalFact
restatementsByMovement
```

Performance tests must catch accidental N+1 behavior.

---

# 33. REQUIRED DATABASE INDEXES

These are implementation requirements subject to final SQL Server execution-plan tuning.

Do not duplicate equivalent existing indexes.

## 33.1 IvTrxHistory physical tail

Add indexes equivalent to:

```text
(CompanyCode, BranchCode, FromBalLocId, TrxDtTime, Id)
WHERE FromBalLocId IS NOT NULL
```

and:

```text
(CompanyCode, BranchCode, ToBalLocId, TrxDtTime, Id)
WHERE ToBalLocId IS NOT NULL
```

INCLUDE the quantities, posting/reversal identity and item fields used by tail replay.

Query FROM and TO legs separately / UNION ALL at scale rather than relying on one large OR predicate.

## 33.2 StockPosting effective/watermark lookup

Current configuration has:
- branch PostingSequence;
- request;
- source revision/role;
- reversal

but no EffectiveAt-leading branch index.

Add/verify an index equivalent to:

```text
(CompanyCode, BranchCode, EffectiveAt, PostingSequence)
```

with sealed state available to the query.

A filtered SQL Server index on sealed postings may be preferable if the actual plan proves it.

This supports:
- period-close watermark;
- replay range/watermark verification;
- chronological audit lookup.

## 33.3 StockValuationFact item tail

Existing:

```text
CompanyCode + BranchCode + ItemCode + EffectiveAt
```

is the correct base.

Tune INCLUDE columns from execution plans for:
- Id;
- StockPostingId;
- InventoryHistoryId;
- Direction;
- BaseQty;
- CostAmount;
- MovementCode;
- ValuationStatus;
- Original/Reverses ids;
- source owner identity.

Existing `IX_StockValuationFact_History` must remain useful for active-generation lookup.

## 33.4 Sales COGS owner

Add:

```text
CompanyCode
BranchCode
SourceDocumentType
SourceDocumentNo
SourceDocumentLine
ItemCode
```

with Direction/ValuationStatus/cost fields included where beneficial.

Current source index leading with `SourceDocumentId` does not cover GP/COGS owner lookup.

## 33.5 Sales return allocation

Add/verify:

```text
CompanyCode
BranchCode
OriginalOwnerDocumentNo
OriginalValuationFactId
```

for active allocation resolution.

## 33.6 Production dependency forward traversal

Add:

```text
PrPoolDependency(
    ContributorMovementId,
    ConsumerMovementId,
    Id)
```

including:
- StockPostingId;
- ReversesDependencyId.

Current configuration leads with Consumer for its main pair index; forward descendant traversal needs Contributor first.

## 33.7 Production movement allocation

Add at least:

```text
PrProductionMovementAllocation(
    ReceiptMovementId,
    Id)
```

and verify whether:

```text
OutboundMovementId, Id
```

also requires a separate index.

## 33.8 Production material Inventory link

Add a filtered/composite index equivalent to:

```text
CompanyCode
BranchCode
InventoryHistoryId
```

for non-null InventoryHistoryId.

Include Production movement/pool/WO identities needed by cost propagation.

## 33.9 Production cost restatement

Add:

```text
ProductionBalLotMovementId, Id
```

plus:
- replay-run/root posting lookup;
- unique filtered reversal-parent identity.

## 33.10 Procurement replay lookup

Execution-plan verify an index for active PurchaseCostAdjustment lookup by:

```text
CompanyCode
BranchCode
ItemCode / source document
StockPostingId or InventoryAdjustmentFactId
```

Do not add blindly if existing source/FK indexes already cover the real query.

PurchaseReceiptCostSettlement already has useful:
- receipt;
- invoice;
- reversal

indexes; replay queries must use those shapes.

## 33.11 Index acceptance rule

Every new index requires:
- concrete replay query;
- actual SQL Server plan;
- logical-read comparison;
- no equivalent useful-prefix duplicate;
- acceptable insert/update overhead.

# 34. PERFORMANCE ALGORITHM RULES

Mandatory:

- no full branch history scan for posting;
- no full item lifetime replay;
- no full Production database traversal;
- no unbounded recursive query without visited set;
- no per-row DB round trips;
- no silent `Take(20)`/pagination cap in correctness logic;
- no loading giant tables merely to filter in .NET;
- no branch lock during UI preview;
- no branch lock during initial impact discovery.

Correctness queries may not truncate evidence.

---

# 35. PERFORMANCE TELEMETRY

For every replay operation log structured metrics:

```text
ReplayRequestId
Company
Branch
RootDocumentType/No
EffectiveAt
PlanningMs
LockedExecutionMs
TotalMs
BranchSequenceWatermark
AffectedBalLocCount
PhysicalTailRowCount
AffectedItemCount
ValuationFactCount
FifoLayerCount
FifoConsumptionCount
ProductionMovementCount
ProductionDependencyCount
SalesOwnerCount
PurchaseAdjustmentCount
FactsReversed
FactsReplaced
RestatementCount
ReplanRetryCount
Success/FailureCode
```

Never log confidential commercial data unnecessarily.

Use these metrics to detect growth regressions before users feel them.

---

# 36. PERFORMANCE TEST DATABASE

Create a SQL Server integration/performance harness containing millions of unrelated historical rows.

Affected-tail scenarios:

```text
~100
~1,000
~10,000
~50,000 stress
```

Historical background target:

```text
5–10 million IvTrxHistory
5–10 million StockValuationFact
realistic FIFO layers/consumptions
realistic Production dependency graph
realistic Purchase settlement/adjustment chains
```

The purpose is to prove runtime depends on the **affected tail**, not total database age.

## 36.1 Mandatory plan/IO gates

For narrow replay:

- no full IvTrxHistory scan;
- no full StockValuationFact scan;
- no full Production movement scan;
- no N+1 query growth;
- expected range/seek indexes used;
- logical reads scale with affected tail.

## 36.2 Branch-lock hold metric

Measure separately:

```text
PlanningMs
LockedExecutionMs
```

The 15-second applock timeout is a wait limit for competing writers.

Certification must define an approved production-class baseline that leaves material headroom so normal competing stock posts do not routinely hit that 15-second wait.

Do not use lock-timeout increase as the optimization.

## 36.3 Supported online replay envelope

Determine a certified maximum online affected-tail size from:
- SQL Server plan;
- lock hold;
- memory allocation;
- request timeout;
- production-class hardware.

If a pathological plan exceeds the certified online envelope:

```text
fail before mutation
```

with a controlled administrative message.

Never partially apply the replay.

The 50k case is a stress/correctness case; it does not automatically mean every 50k replay must be offered interactively if it cannot satisfy the online lock SLO.

## 36.4 Performance regression gate

Record a checked-in benchmark baseline.

A material regression in:
- logical reads;
- query count;
- peak memory;
- locked execution duration

requires explicit review before merge.

Do not approve merely because functional tests remain green.

# 37. OPTIONAL CIRCUIT BREAKER — SAFETY ONLY

A configurable very-high replay safety ceiling may exist to protect the server from pathological/corrupt dependency explosions.

If exceeded:

```text
fail BEFORE mutation
```

with an administrative message.

Never partially replay or silently skip rows.

This ceiling is not a normal business restriction and must be set well above expected open-period volume.

---

# 38. FEATURE GATE / ROLLOUT

Use an internal environment-controlled feature:

```text
OpenPeriodBackdatedReplayEnabled
```

This is NOT an ordinary ERP user option.

## 38.1 Environment defaults

The intended configuration is:

```text
Development / Local     = ON
Test                     = ON
UAT                      = ON
Production               = OFF until readiness approval
```

The purpose is to let developers, testers and the product owner manually exercise the complete backdated-posting workflow before Live deployment.

Do not require all Production-readiness gates to pass before manual testing in non-production environments.

## 38.2 Non-production manual testing is explicitly allowed

In Development/Test/UAT the feature should be enabled so the team can manually test realistic end-to-end scenarios such as:

```text
01 Oct  GR RM001
03 Oct  IP RM001
05 Oct  Daily Production -> WIP
08 Oct  FG Receipt
10 Oct  DO
12 Oct  Invoice

16 Oct:
POST late GR dated 02 Oct
```

Then verify:

```text
Inventory quantity
Inventory valuation
IP cost
WIP cost
FG cost
DO / Invoice COGS
Gross Profit
Sales return cost
Procurement PPV
Rollback
Month-end close
```

Manual testing is part of readiness evidence; it is not a substitute for automated regression/concurrency/performance tests.

## 38.3 Production remains protected

Production must remain:

```text
OpenPeriodBackdatedReplayEnabled = false
```

until the complete Enablement Readiness Suite produces:

```text
FINAL RESULT: SAFE TO ENABLE
```

with:

```text
Critical failures = 0
High failures     = 0
All mandatory gates = PASS
```

## 38.4 Who turns Production ON

Production activation is a deployment/admin action performed by the development/deployment owner.

Normal ERP users must not be able to enable or disable this feature.

Recommended production activation flow:

```text
1. deploy tested implementation
2. keep Production feature OFF
3. run Production-like readiness validation / smoke checks
4. review BackdatedReplayReadinessReport.md
5. approve release
6. change production configuration to ON
7. restart/reload configuration as required
8. run post-enable smoke test
```

## 38.5 Emergency kill switch

After Production certification the intended long-term state is:

```text
Production = ON
```

Keep the configuration flag available as an emergency kill switch.

Turning it OFF must return users to the conservative current behavior:

```text
later dependent movement exists
=> backdated POST blocked
```

It must not corrupt or invalidate already-created replay evidence.

## 38.6 Optional company/branch override in non-production only

For Test/UAT convenience, a Company/Branch-level override may be supported so one test tenant can exercise replay without enabling every test tenant.

If implemented:

```text
Environment must be non-Production
AND global feature enabled
AND optional company/branch override enabled
```

Do NOT expose this override to ordinary users.

Production should use one centrally controlled deployment configuration unless a future controlled rollout requirement is explicitly designed and reviewed.

# 39. IMPLEMENTATION PHASES

## Phase 0 — repository characterization / prerequisite fixes

No behavior enablement.

Deliver:
- every stock-driving POST/ROLLBACK entry;
- every SAVE/POST future-date check;
- every `IvBalLoc.TransDate` historical reader;
- every Inventory valuation consumer;
- every Production raw-cost consumer;
- every PI settlement/adjustment consumer;
- every COGS/return consumer;
- every rollback assuming original generation only.

Also fix/prove the month-end FIFO active-issue classification described in Section 45.

## Phase 1 — schema/index/lineage infrastructure

Implement:
- required indexes;
- `StockReplayRun`;
- active physical resolver;
- active valuation-generation resolver;
- active value-adjustment resolver;
- active purchase settlement/adjustment resolvers;
- active Sales return resolver;
- Production effective-cost restatement/resolver;
- telemetry.

Feature remains OFF.

## Phase 2 — chronology router + strict physical tail

Implement:
- fast vs replay router;
- strict as-of rewind;
- no negative chronology;
- latest-active TransDate;
- historical lookup fixes;
- business-time normalization.

Keep old financial backdate blocker until next phases are complete.

## Phase 3 — replay posting context + Inventory financial engines

Implement:
- replay line allocator;
- event-level fact EffectiveAt;
- replay-specific completion seam;
- WAC;
- FIFO;
- Standard;
- value-only events;
- active-generation rollback.

## Phase 4 — Procurement settlement/value replay

Implement:
- late-GR PI settlement remapping;
- PI active settlement generation;
- PPV inventory/consumed split;
- PI rollback;
- Purchase CN;
- Purchase CN rollback.

## Phase 5 — Production propagation

Implement:
- IP;
- Daily Production;
- WIP/process handoff;
- PRTN;
- PADJ;
- WO Transfer;
- FG;
- Production allocation/dependency replay;
- effective-cost readers.

## Phase 6 — Sales propagation

Implement:
- DO;
- direct Invoice shipment;
- LinkDo;
- CDN stock return;
- SalesReturnCostAllocation;
- COGS;
- GP.

## Phase 7 — replay-aware rollback

Only now remove manual newest-first rollback dependency for replay-supported OPEN-period events.

## Phase 8 — close/reconcile/concurrency/performance certification

Run all:
- functional suites;
- SQL Server race tests;
- failure injection;
- month-end close/reopen;
- multi-million-row benchmark.

Feature remains OFF until ALL gates pass.

# 40. TRANSACTION-SCOPE MATRIX

| Area | Transaction | Future SAVE | Future POST | Backdate OPEN POST | Cost replay |
|---|---|---:|---:|---:|---|
| Inventory | MR/GR | YES | NO | YES | YES |
| Inventory | MI/Scrap/VR | YES | NO | YES | YES |
| Inventory | Transfer | YES | NO | YES | YES |
| Inventory | Adjustment | YES | NO | YES | YES |
| Sales | DO shipment | YES | NO | YES | COGS |
| Sales | direct Invoice shipment | YES | NO | YES | COGS |
| Sales | Invoice LinkDo | YES | NO | commercial post; DO owns stock | DO COGS |
| Sales | CDN ReturnStock | YES | NO | YES | original-sale return cost |
| Sales | financial-only CDN | normal rule | normal rule | no physical replay | no stock |
| Production | IP / Material Issue | YES | NO | YES | Inventory→Production |
| Production | Daily Production | YES | NO | YES | WIP/material |
| Production | Stock Return | YES | NO | YES | Production→Inventory |
| Production | Stock Adjustment | YES | NO | YES | pool authority |
| Production | WO Transfer | YES | NO | YES | exact OUT/IN |
| Production | FG Receipt | YES | NO | YES | WIP→FG |
| Procurement | PI/CN value adjustment | normal draft | NO if future costing date | YES | PPV/value replay |

All are still subject to ordinary permissions/status/master-data/business validation.

---

# 41. REQUIRED FUNCTIONAL TESTS

## Date / Business Time

For every stock-driving module:
- future SAVE remains NEW;
- future POST fails;
- POST on Business Date succeeds;
- past OPEN-period POST routes to replay when required;
- CLOSED-period POST fails;
- company timezone is used, not server-local time;
- Daily Production future POST specifically covered.

## Physical quantity

- late receipt with later issue;
- late issue with later receipt;
- current qty sufficient but historical qty insufficient => fail;
- historical qty sufficient but current physical qty insufficient => fail;
- first negative intermediate event identified;
- lot/status/location isolation;
- transfer;
- same-time candidate deterministic order;
- rollback old receipt causing later negative => fail;
- `TransDate` remains latest active;
- Production reversal at later action time does not distort active historical business position.

## Valuation generation

- one WAC fact generation;
- WAC transfer OUT+IN generation;
- FIFO one-layer issue;
- FIFO multi-layer issue;
- FIFO transfer multi-split OUT + IN;
- replay changes FIFO split count;
- old generation fully reversed;
- exactly one active generation remains;
- competing two-active-generation corruption => reconciliation fail;
- rollback reverses current generation, not original generation.

## Replay fact dates

For a root event on date A correcting later events on B/C:

- correction facts for B remain in period B;
- correction facts for C remain in period C;
- `CreatedAtUtc`/root PostedAt remains actual action time;
- month-end snapshot buckets correctly.

## WAC

- late receipt recosts later MI;
- late receipt recosts IP;
- inbound FG cost restatement applies immediately after receipt;
- later PPV;
- customer return;
- zero qty/value invariant.

## FIFO

- backdated receipt before issue;
- partial/full layer consumption;
- issue split replacement;
- existing receipt cost restatement without second physical layer;
- customer-return layer cost restatement;
- PPV;
- layer consumption reversal/replacement;
- final state/layer reconciliation.

## Standard

- event-date Standard revision;
- Standard revaluation later in tail;
- Production Standard variance;
- Sales return Standard variance.

## Cost-method boundary

- normal open-period replay sees one method;
- injected legacy/corrupt cutover inside replay range => hard block.

## Procurement settlement

Critical scenarios:

```text
GR 05-Oct
PI 10-Oct settles GR 05-Oct

late GR 02-Oct posted on 13-Oct
=> PI settlement remaps according to existing
   BusinessDate/PostingSequence/FactId order
```

Then verify:
- old settlement reversed append-only;
- replacement settlement active;
- PPV recalculated;
- FIFO inventory/consumed variance corrected;
- PI rollback after replay;
- PI -> CN after replay;
- CN rollback after replay;
- no duplicate active settlement quantity.

## Production

- IP cost correction;
- Daily Production;
- multiple process WIP;
- PRTN;
- PADJ all authority types;
- WO Transfer OUT=IN;
- FG;
- dependency restatement;
- quantity allocation restatement;
- cycle detection;
- frozen conversion facts unchanged.

## Sales

- DO COGS;
- direct Invoice COGS;
- LinkDo no duplicate;
- CDN financial-only untouched;
- CDN ReturnStock active original COGS;
- replay then return;
- return then replay;
- rollback after replay;
- GP only current active outbound generation.

## Month-end

- replay reversal facts do not get misclassified as active FIFO issues;
- FIFO receipt rollback followed by close;
- replayed FIFO issue followed by close;
- snapshot qty/value equals state/layers;
- close/reopen/reclose remains deterministic.

# 42. CONCURRENCY / FAILURE-INJECTION TESTS

In addition to the cases below, explicitly test the SQL session-context contract:

```text
root posting context established
replay writes guarded tables
exception thrown mid-replay
transaction rolls back
session context cleared by disposal/recovery
next independent stock post succeeds
```

Also test that no nested replay posting accidentally clears the root context.


SQL Server required.

Test:

- normal current post vs backdated post same item;
- two backdated posts same item;
- backdated post vs rollback;
- backdated Inventory receipt vs DO post;
- backdated GR vs IP;
- replay vs FG;
- replay vs Purchase Invoice cost adjustment;
- two Production descendants same pool;
- client retry with same request;
- branch sequence changes between planning and lock;
- rowversion changes between planning and lock;
- deadlock/timeout;
- exception after physical calculation before persistence;
- exception after valuation reversal creation;
- exception after Production restatement;
- exception before Sales allocation restatement;
- exception before seal.

After every injected failure:

```text
document status unchanged
IvBalLoc unchanged
StockCostState unchanged
FIFO state unchanged
Production pool unchanged
no orphan replay facts
no partial reversal chain
```

---

# 43. EXISTING REGRESSION SUITES — MANDATORY

At minimum:

```text
ErpWeb.Tests/Inventory/Inquiry/InventoryAsOfStockServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingSqlServerConcurrencyTests.cs
ErpWeb.Tests/Inventory/Transaction/IvPeriodCloseGuardTests.cs
ErpWeb.Tests/Inventory/Transaction/IvPeriodCloseServiceTests.cs

ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs
ErpWeb.Tests/Production/Transaction/ProductionAbsorbedCostCalculatorTests.cs
ErpWeb.Tests/Production/Transaction/ProductionCostGateTests.cs
ErpWeb.Tests/Production/Transaction/ProductionCostReadinessTests.cs
ErpWeb.Tests/Production/Transaction/ProductionDailyOutputCoreTests.cs
ErpWeb.Tests/Production/Transaction/ProductionMaterialIssueSequenceOverlapTests.cs
ErpWeb.Tests/Production/Transaction/ProductionOutputConsumeVarianceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionOutputEntryServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionPooledCostTraceMathTests.cs
ErpWeb.Tests/Production/Transaction/ProductionStockAdjustmentServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferSqlServerTests.cs

ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaInvoiceSqlServerConcurrencyTests.cs
ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaCdnSqlServerConcurrencyTests.cs

ErpWeb.Tests/Procurement/Transaction/PoInvoiceServiceTests.cs
ErpWeb.Tests/Procurement/Transaction/PoInvoiceSqlServerConcurrencyTests.cs
ErpWeb.Tests/Procurement/Transaction/PoCdnServiceTests.cs
ErpWeb.Tests/Procurement/Transaction/PoCdnSqlServerConcurrencyTests.cs
```

Do not weaken tests simply because old chronology blockers are removed.

Replace old “later movement must fail” assertions with the new correct conditions:
- valid tail => allowed/replayed;
- invalid historical qty/cost dependency => rejected.

---

# 44. RECONCILIATION GATES BEFORE COMMIT

For all affected physical slices:

```text
replayed closing qty == IvBalLoc target closing qty
```

For all affected financial item pools:

```text
active fact qty == StockCostState qty
active fact value == StockCostState value
```

For FIFO:

```text
open layer qty == StockCostState qty
open layer value == StockCostState value
consumption evidence complete
```

For Production:

```text
movement net qty == ProductionBalLot.BaseQty
effective movement net value == ProductionBalLot.TotalCost
ProductionPoolValuation qty/value == live pool qty/value
WO Transfer OUT value == IN value
```

For Production→Inventory:

```text
effective FG/PRTN value == corresponding Inventory financial authority
```

except documented Standard-cost variance separation.

For Sales:

```text
active shipment valuation == COGS owner facts
return allocations do not exceed active outbound qty/value
```

For Procurement:

```text
active cost-adjustment chain reconciles settlement amount
FIFO inventory + consumed variance = total adjustment
```

Any mismatch:

```text
THROW
ROLLBACK ENTIRE SQL TRANSACTION
```

---

# 45. MONTH-END CLOSE INTEGRATION

Period close is a hard replay certification gate.

Do not weaken it.

## 45.1 Current repository prerequisite fix

`StockValuationSnapshotBuilder` currently builds FIFO consumption-completeness candidates from facts where:

```text
CostMethod == FIFO
Direction < 0
BaseQty > 0
```

excluding only `COST_METHOD_CUTOVER_OUT`.

A FIFO **receipt reversal** is also direction `-1`, but it has:

```text
ValuationStatus = REVERSED
```

and correctly has no FIFO issue-consumption rows.

Replay will create additional reversal facts.

Before enablement, update the close validator so FIFO consumption completeness applies only to the correct **active valued issue generation**, not reversal facts.

At minimum the logic must include active/VALUED generation semantics; preferably reuse the central valuation-generation resolver / movement classifier.

Do not merely special-case one `_REVERSAL` string.

## 45.2 Snapshot accounting

Financial close intentionally sums:
- originals;
- reversals;
- replacement/value-restatement facts

by signed direction/value.

Therefore every replay correction fact must carry the corrected business event's:

```text
EffectiveAt
BusinessDate
PeriodKey
```

not the replay action date.

## 45.3 Final close checks

Continue to prove:
- no UNVALUED required facts;
- financial closing qty/value non-negative;
- zero qty has no residual value;
- `StockCostState` reconciles;
- FIFO layers reconcile;
- active FIFO issues have exact active consumption evidence;
- Standard FG variance evidence exists;
- physical quantity snapshot equals financial quantity;
- Production costing readiness is clean.

## 45.4 Closed-period immutability

A normal valid open replay should never have a later CLOSED period because close/reopen is contiguous.

If legacy/corrupt data reveals a closed-period dependency:

```text
BLOCK
```

and require formal reconciliation/reopen.

Never silently push that correction into the current period.

# 46. USER EXPERIENCE

Normal late transaction:

```text
User presses POST
```

No manual rollback wizard.

Possible success detail:

```text
Posted successfully.
Historical stock and costing were recalculated from 02/10/2026.
12 later cost movements were restated.
```

If invalid:

```text
Cannot post MI dated 03/10/2026.

RM001 / WH01 / L001 would become -3.0000
after DO DO000125 dated 06/10/2026.

Available before DO: 5.0000
Required by DO: 8.0000
```

Do not expose internal replay complexity unless useful.

---

# 47. FILES / AREAS EXPECTED TO CHANGE

This is not exhaustive; Phase 0 must produce the final impact manifest.

Core areas already confirmed:

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.Chronology.cs
ErpWeb.Core/Inventory/InventoryAsOfStockService.cs
ErpWeb.Core/Inventory/IvSpShipmentService.cs
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs
ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs

ErpWeb.Core/StockLedger/StockPostingCoordinator.cs
ErpWeb.Core/StockLedger/InventoryValuationService.cs
ErpWeb.Core/StockLedger/StockPostingGuards.cs

ErpWeb.Core/Purchase/PurchaseReceiptCostSettlementService.cs
ErpWeb.Core/Purchase/PurchaseCostAdjustmentPostingService.cs
ErpWeb.Core/Purchase/PurchaseCdnCostAdjustmentService.cs
ErpWeb.Core/Purchase/PoInvoiceService.cs
ErpWeb.Core/Purchase/PoCdnService.cs

ErpWeb.Core/Production/ProductionMaterialIssueService.*
ErpWeb.Core/Production/ProductionOutputService.*
ErpWeb.Core/Production/ProductionStockReturnService.*
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService*
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.*
ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionContributionAvailabilityService.cs
ErpWeb.Core/Production/ProductionStockWriter.cs

ErpWeb.Core/Sales/SaDoService.cs
ErpWeb.Core/Sales/SaInvoiceService.cs
ErpWeb.Core/Sales/SaCdnService.cs
ErpWeb.Core/Sales/SalesGrossProfitInquiryService.cs

ErpWeb.Model/Data/AppDbContext.cs
StockLedger / Inventory / Production / Sales configuration files
new replay services/configurations/migrations
```

---

# 48. MUST NOT DO

The AI Coding Agent MUST NOT:

- simply delete `ValidateNoLaterDayMovementsAsync`;
- simply delete `RejectBackdatedPoolsAsync`;
- expose a generic `skipChronologyChecks` flag;
- run normal POST and “recost later”;
- use server-local time instead of company Business Date;
- allow future POST;
- alter CLOSED business chronology;
- replay all lifetime history;
- call period-close full snapshot builder per transaction;
- hold branch lock during initial dependency discovery;
- assume one valuation fact per `IvTrxHistory`;
- reverse only one FIFO split;
- treat a reversal fact as an active issue because it is a leaf;
- call normal `NewFact`/`StockValueAdjustmentWriter` for replay without event-level EffectiveAt support;
- stamp every correction with the root transaction date;
- open nested StockPosting contexts without proven session-context push/restore;
- clear the root session context before all guarded writes finish;
- call normal `CompleteInTransactionAsync` and accidentally value replayed history a second time;
- reuse source PostingLineNo values and violate posting-line uniqueness;
- manufacture duplicate physical receipts just to fix receipt cost;
- mutate/delete sealed `StockValuationFact`;
- rewrite immutable Production posted evidence;
- change FIFO `OriginalValue` as a shortcut when accumulated/restatement semantics are required;
- ignore Purchase Invoice settlement remapping after a late GR;
- assume PI rollback roots have never been replay-reversed;
- ignore PPV/consumed variance;
- cross a cost-method cutover in online replay;
- use `IvBalLoc.TransDate` alone as historical stock availability;
- clamp negative replay qty to zero;
- restore stale price snapshots over later valid activity;
- ignore Sales return allocation lineage;
- duplicate COGS for LinkDo Invoice;
- introduce N+1 replay queries;
- use recursive one-row-at-a-time Production traversal;
- add correctness row caps;
- increase applock timeout instead of fixing replay performance;
- enable Inventory replay before Procurement/Production/Sales propagation is complete;
- commit a partial replay.

# 49. HARD ENABLEMENT GATES

These gates control **Production activation**, not Development/Test/UAT manual testing.

Development/Test/UAT may run with:

```text
OpenPeriodBackdatedReplayEnabled = true
```

while these gates are still being executed and defects are being fixed.

Production remains OFF until every item below passes.

## V2 ledger
- ACTIVE epoch;
- compatibility PASS;
- no unsealed/orphan posting;
- baseline reconciliation clean.

## Business date / periods
- company timezone Business Date;
- future POST blocked;
- CLOSED period blocked;
- cutover-boundary invariant proven.

## Physical
- strict as-of tail;
- no negative intermediate qty;
- current + historical availability;
- latest-active `TransDate`;
- active reversal lineage.

## Valuation generations
- WAC generation;
- transfer OUT+IN generation;
- FIFO multi-split generation;
- exactly one active generation;
- replay-aware rollback.

## Replay posting context
- one valid root session write context;
- replay line allocator;
- event-level EffectiveAt;
- replay-specific completion invariant.

## Procurement
- late-GR PI settlement remap;
- PPV;
- FIFO consumed/inventory split;
- PI rollback after replay;
- CN/rollback after replay.

## Production
- IP;
- Daily Production;
- PRTN;
- PADJ;
- WO Transfer;
- FG;
- dependency/allocation replay;
- effective-cost resolver;
- no WIP/FG drift.

## Sales
- DO;
- direct Invoice;
- LinkDo;
- CDN;
- return allocation;
- COGS/GP active generation.

## Month-end
- FIFO reversal-classification prerequisite fixed;
- close/reopen/reclose green;
- snapshot/state/layer reconciliation green.

## Concurrency
- watermark;
- RowVersion;
- session-context;
- failure injection;
- bounded re-plan;
- SQL Server races green.

## Performance
- required indexes deployed;
- StockPosting EffectiveAt index verified;
- no full-history scan;
- no N+1;
- multi-million-row benchmark green;
- online replay lock-hold SLO approved;
- ordinary competing writer timeout rate acceptable.

## Manual UAT

Mandatory manual end-to-end scenarios must be signed off, including:

- backdated Inventory receipt with later Inventory issues;
- backdated movement feeding IP -> WIP -> FG -> Sales;
- backdated movement affecting Purchase Invoice settlement/PPV;
- backdated rollback;
- invalid historical negative-stock case;
- future transaction Save YES / Post NO;
- closed-period POST blocked;
- month-end close after replay.

The readiness report must record:

```text
Manual UAT = PASS
Tester / reviewer
Test environment
Test database/build
Date
Scenario results
Known issues
```

If any Production gate fails:

```text
Production feature remains OFF
```

This does NOT require disabling the feature in Development/Test/UAT; those environments remain available for debugging and manual verification.

# 50. FINAL APPROVAL STATEMENT

This strict final plan remains based on CURRENT `productionv2`:

```text
a5dead323a0e5b19d7b95b457d1a7bd9c0d98cac
```

The plan is **FINAL APPROVED FOR IMPLEMENTATION — 10/10 as an implementation plan**.

The rollout rule is now explicitly:

```text
Development / Local = ON
Test                = ON
UAT                 = ON
Production          = OFF until readiness PASS
```

This allows the developer/product owner to manually test the real feature before Live.

Production activation requires:

```text
BackdatedReplayReadinessReport.md
FINAL RESULT: SAFE TO ENABLE
Critical = 0
High = 0
All mandatory gates PASS
Manual UAT PASS
```

After that:

```text
Production = ON
```

and the feature should normally remain ON permanently.

The configuration flag remains available only as an emergency kill switch.

Normal ERP users must never control this switch.

The core implementation invariants remain unchanged:

> **Normal chronological transactions keep the existing proven fast path.**

> **True backdated dependency cases use bounded replay.**

> **Future Transaction Date may SAVE as NEW but cannot POST before Business Date reaches it.**

> **OPEN period supports safe late posting. CLOSED periods remain frozen.**

> **Inventory, Procurement costing, Production WIP/FG and Sales COGS/returns are one connected replay graph.**

> **Physical quantity must remain valid at every historical step.**

> **Immutable evidence remains append-only.**

> **Performance scales with the affected tail, not database lifetime.**

> **Production is enabled only after automated + SQL Server + performance + manual UAT readiness evidence is complete.**

Any implementation that deviates from these invariants returns to review.

