# ProductionV2 --- Shared BOM WIP Consumption Policy

## FINAL DEEP REVIEW --- REPOSITORY-GROUNDED AI CODING AGENT PLAN

**Repository:** `mokth/net10projectTemplate` · **Branch:**
`productionv2`\
**Review verdict:** **APPROVED DESIGN / IMPLEMENTATION WITH MANDATORY
VERIFICATION GATES**\
**Risk classification:** HIGH --- production quantity, valuation,
genealogy and rollback\
**Compatibility:** Strict remains the default and original posting path.

## Executive decision

Implement **company-scoped, opt-in Shared BOM WIP** by widening only the
source-lot query for Daily Production `InternalRouteWip`. **Do not**
widen automatic previous-process handoff; **do not** change manual
Material Issue or purchased/external sources; **do not** change BOM
requirement, cost formula, V2 stock-ledger valuation or original strict
branch.

The existing V2 valuation/genealogy engine is already substantial. Reuse
it; do not create parallel allocation, valuation or trace tables unless
proven necessary.

### Critical correction to prior plan

The previous conditional plan suggested new cross-WO genealogy and
rollback mechanisms might be necessary. The repository already
implements:

-   `ProductionPoolValuationService.RecordAsync` --- pool valuation
    evidence, receipt-to-outbound `ProductionMovementAllocations`, and
    `ProductionPoolDependencyRows`.
-   `ProductionPoolValuationService.HasActiveDependentsAsync` ---
    graph-based guard for active downstream movements.
-   `ProductionOutputService.Rollback.cs` --- dependency check plus
    `ProductionBalLotSignedQty.CanRollbackProduce`, original-source
    consume reversal and conversion-cost reversal.

**Therefore do not duplicate these mechanisms.** Verify that they work
for cross-WO Shared BOM WIP and add narrowly targeted fixes only if
integration tests prove gaps.

## 1. Verified source inventory

  ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
  File                                                              Confirmed behavior                                                                                                    Required action
  ----------------------------------------------------------------- --------------------------------------------------------------------------------------------------------------------- -----------------------
  `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor` /    Daily Production UI uses `IProductionOutputService`; BOM material lines keyed by `WorkOrderMaterialId`;               Keep normal workflow.
  `.razor.cs`                                                       previous-process handoff is not editable.                                                                             Add only read-only
                                                                                                                                                                                          source inquiry in
                                                                                                                                                                                          Shared mode.

  `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`       `PostAsync` performs posting under `BranchStockTransactionLock`, work-order/operation/material locks, V2 coordinator. Preserve posting
                                                                                                                                                                                          transaction and
                                                                                                                                                                                          ordering.

  Same, around lines 217--225                                       `InternalRouteWip` calls                                                                                              This is the **only**
                                                                    `LockBalLotsAsync(db, company, branch, Wip, order.Uid, material.ProducingRouteStepId, material.ComponentCode, ct)`.   intended source-scope
                                                                                                                                                                                          branch.

  Same, around lines 114--145                                       Previous-process handoff uses `LockHandoffLotAsync(... order.Uid, priorOperation.Uid ...)`.                           **Never change.**

  Same, around lines 818--883                                       `LockOrCreateWipLotAsync` reuses balance by company, branch, WO, route step, item and lot; multiple produce movements Preserve lot identity;
                                                                    can contribute to one lot.                                                                                            do not claim every lot
                                                                                                                                                                                          corresponds to a single
                                                                                                                                                                                          production document.

  Same, around lines 714--816                                       Handoff lots also have `Kind=Wip`, but `ProducingRouteStepId == null`, `WorkOrderOperationId` populated, and          Shared query must
                                                                    handoff-specific lot number.                                                                                          explicitly **exclude
                                                                                                                                                                                          handoff lots**.

  Same, around lines 310--423                                       Existing proportional `TotalCost` draw, exact final depletion, `StockLedgerPrecision.Money`, material and balance     Reuse unchanged.
                                                                    movements, conversion-cost roll-up.                                                                                   

  Same, around lines 490--570                                       Absorbed conversion-cost facts and V2 invariant assertion.                                                            Preserve.

  `ErpWeb.Core/Production/ProductionOutputService.Rollback.cs`      Source-lot restoration, `HasActiveDependentsAsync`, signed-prefix rollback guard, conversion-cost handling.           Test cross-WO; avoid
                                                                                                                                                                                          speculative rewrites.

  `ErpWeb.Core/Production/ProductionPoolValuationService.cs`        Pooled-average valuation evidence, contribution allocations, dependency graph, verified/unvalued status,              **Reuse as cost/lineage
                                                                    reconciliation.                                                                                                       authority.**

  `ErpWeb.Model/Entities/Production/ProductionBalLot.cs`            `WorkOrderId`, `ProducingRouteStepId`, `ProductionLocationId`, `BalanceStage`, `StockStatusCode`, `OriginType`,       Build eligibility from
                                                                    `BaseUom`, `TotalCost`, `BaseQty`, `LastMovementDate`.                                                                real fields;
                                                                                                                                                                                          distinguish legacy
                                                                                                                                                                                          nulls.

  `ErpWeb.Core/Settings/AppSettingCatalogue.cs`                     Catalogued `AdSmParam` settings with module/key, allowed tokens, default and scope; no new column needed.             Add company-scoped
                                                                                                                                                                                          token setting in
                                                                                                                                                                                          catalogue.

  `ErpWeb.Core/Production/ProductionMaterialAllocationService.cs`   Separate warehouse/manual material issue allocation.                                                                  **Out of scope**.
  ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

## 2. Global setting

Create a
`ProductionKeys.WipConsumptionPolicy = "WIP_CONSUMPTION_POLICY"`
catalogue entry:

-   Module: `Production`
-   Scope: `Company` **only**
-   Type: token with allowed values `STRICT` and `SHARED_BOM_WIP`
-   Default: `STRICT`
-   Backing: existing `AdSmParam` row mechanism
-   UI label: **WIP Consumption Source**
-   Description: `STRICT` uses current Work Order/route-step WIP;
    `SHARED_BOM_WIP` allows BOM-required WIP from other eligible Work
    Orders within the same company and branch.

Add to `AppSettingCatalogue.All`, its existing settings tests, and Admin
Settings. Use existing settings provider API; inspect it and the DI
registration before implementation. Invalid/missing values must resolve
to `STRICT`. Company setting **does not mean cross-branch**. No schema
migration for this setting.

**Draft semantics:** Decide and document whether the mode is captured at
draft creation or resolved at posting. Recommended safest behavior:
persist mode for newly created drafts, and post under that recorded
mode, while keeping existing drafts STRICT. This may require an
**additive, defaulted** policy column on `PrProductionOutput` if no
existing metadata field supports it. Do not silently reinterpret an old
draft after a setting change. The agent must inspect current schema
conventions and implement an explicit, tested choice.

## 3. Exact posting change

Inside `ProductionOutputService.Posting.cs`, after existing validation
and before source selection, determine effective policy using the
approved draft/post contract.

**Existing STRICT call must remain the same:**

``` csharp
lots = await LockBalLotsAsync(
    db, scope.CompanyCode, scope.BranchCode!,
    ProductionBalLotKinds.Wip, order.Uid,
    material.ProducingRouteStepId, material.ComponentCode,
    cancellationToken);
```

Only for `material.SupplySource == InternalRouteWip` and policy
`SHARED_BOM_WIP`, call a **new** `LockEligibleSharedBomWipLotsAsync`.
Keep all downstream `consumeFacts`, `remainingBaseByLot`, movement
creation, cost calculation, valuation evidence and ledger posting
unchanged.

**Important:** Current code checks
`material.ProducingRouteStepId != null` before strict source selection.
Preserve that for STRICT. In Shared mode, the material still needs a
valid BOM/source contract, but should not be constrained to the
consuming WO's producing step. Confirm snapshot validations before
altering this check; do not loosen Work Order definition validation.

## 4. Shared lot eligibility --- mandatory

Filter on all verified applicable fields:

1.  `CompanyCode == scope.CompanyCode` and
    `BranchCode == scope.BranchCode`.
2.  `Kind == ProductionBalLotKinds.Wip`.
3.  `ItemCode == material.ComponentCode` (exact).
4.  `ProducingRouteStepId != null` --- **exclude previous-process
    handoff lots**.
5.  `BaseQty > 0`, valid UOM and conversion factor.
6.  `BaseUom` compatible with material base UOM; reject mismatches
    rather than invent conversions.
7.  WIP `OutputType == WipStocked` / `BalanceStage == PROCESS_WIP` when
    these are authoritative for current snapshots; explicitly exclude FG
    staging and non-stocked handoffs. Handle legacy null stage
    conservatively (fail closed if identity cannot be proven).
8.  Valid `StockStatusCode` (`AVAILABLE` where populated); exclude
    blocked/held/quarantined material. Confirm status semantics and do
    not assume null is automatically available.
9.  **ProductionLocationId/site/warehouse rule:** validate
    source/consumer location eligibility against real
    production-location configuration. The original WIP lot has
    `ProductionLocationId`; do not assume all locations in a branch are
    fungible. Company-wide setting does **not** grant cross-location or
    cross-warehouse access. If no approved transfer path exists,
    restrict to the consumer's permitted production location. Obtain
    explicit product decision before broadening geography.
10. `LastMovementDate <= output.ProductionDate`; apply existing
    no-future-stock semantics.
11. Verified valuation: inspect `ProductionPoolValuation`/evidence
    status and **do not consume unvalued/unknown pooled WIP in Shared
    mode**. Check this inside posting under lock, not only preview.
12. Original source movements remain valid and traceable; exclude
    invalid/reversed stock generations as dictated by valuation engine.

Use deterministic source order (existing last movement date, lot number,
UID) after selection; do not alter strict ordering. Select candidate IDs
first and acquire locks in ascending UID using existing
`LockBalLotsByIdsAsync` or a compatible helper, then recheck eligibility
and quantity under lock. Retain `remainingBaseByLot` to avoid duplicate
allocation across BOM lines.

## 5. Costing contract --- NO NEW COST ENGINE

The current code already calculates consumption cost as:

``` text
takeCost =
  full depletion: lot.TotalCost
  partial draw:   Money(lot.TotalCost * takeBase / lot.BaseQty)
```

Then updates lot quantity/value and adds `totalConsumedCost`; production
output value is:

``` text
producedCost = Money(totalConsumedCost + absorbedConversionCost)
```

**Do not modify these formulas.** The valuation service records
`POOLED_AVERAGE` cost evidence, receipt genealogy, source contributions
and dependency edges. Reuse all of them.

**Key distinction:** a lot can contain multiple production receipts, so
its current cost is a pooled-average cost. The genealogy allocation
identifies **which receipts contributed quantities**, but does not imply
every consumed quantity retains its original individual receipt unit
cost. UI must not falsely present producer-specific unit cost if only
pooled average is authoritative.

**Cost assertions:** - Before/after source-lot value delta equals the
consumed movement `TotalCost`. - Sum of all material/handoff consumption
movement costs plus absorbed conversion facts equals produced movement
cost within existing rounding contract. - No verified pool becomes
negative, zero-quantity with stranded value, or `UNVALUED` after an
otherwise valid shared posting. -
`ProductionPostingInvariant.AssertOutputVerifiedAsync` and V2
stock-ledger reconciliation pass. - No fallback to Item Master, latest
purchase price or selling price.

## 6. Traceability and source inquiry

Reuse existing: - `ProductionBalLotMovement` and
`ProductionMaterialMovement` (consumer transaction to consumed lot); -
`ProductionMovementAllocations` (outbound movement to receipt
contribution); - `ProductionPoolDependencyRows` (value dependency); -
`ProductionValuationEvidence` and `ProductionPoolValuation`.

Do not add a second allocation table.

Inquiry should show: consuming Daily Production/WO, component item,
quantity, source WIP lot, originating WO/route step and (where
contribution allocations exist) source produce document(s). If multiple
receipts share one lot, display all contributing receipts; show
allocated genealogy quantities and separately label **actual pooled
consumption cost**. Mask cost behind existing ViewCost permission.

## 7. Rollback and lifecycle

The existing rollback already: 1. checks
`ProductionPoolValuationService.HasActiveDependentsAsync` for produced
movement IDs; 2. checks `ProductionBalLotSignedQty.CanRollbackProduce`;
3. restores original consumed lot IDs and original movement cost; 4.
reverses conversion-cost evidence/ledger movements.

**Required:** test cross-WO scenario before adding code. Example: - WO-A
produces 10 WIP001; WO-B consumes 6; attempt rollback of WO-A's
producer. - Expected: blocked while WO-B's dependent consumption is
active. - Reverse WO-B; then retry WO-A; expected safe if all other
prerequisites pass.

Also test pooled lot with two produce movements, partial consumption and
reversal of only one producer. Confirm `HasActiveDependentsAsync`
catches the dependency via existing graph. Check source WO
delete/reopen/refresh lifecycle operations for indirect dependencies;
add narrow guard only where needed.

**Do not disable dependency checks when company setting returns to
STRICT.** Historical Shared transactions must remain protected.

## 8. Concurrency and dates

Keep `BranchStockTransactionLock`, SQL Server UPDLOCK/HOLDLOCK, V2
posting transaction, work-order lock order, idempotent posting link, and
row-level lot locks. Verify a shared lot from another WO does not
introduce a reverse lock-order deadlock. All posting-time eligibility
checks must run inside the transaction after locking.

Test simultaneous posting from two different WOs for the same remaining
WIP; one must fail cleanly rather than overdraw. Test backdated posts
and subsequent rollback when the source lot's last movement date
changes.

## 9. UI and backwards compatibility

-   STRICT: **zero new mandatory user steps**, same BOM lines, same
    availability and messages where possible.
-   SHARED: same Daily Production entry; optional compact **Source**
    info action for each WIP BOM line. No mandatory WO picker.
-   Preview is advisory only; posting independently locks and validates.
-   Existing manual issue/warehouse stock allocation and
    previous-process handoff remain untouched.
-   No BOM substitution, no automatic transfer across locations, no
    bypass of tolerances or permissions.

## 10. Required agent implementation sequence

**Gate A --- source verification before editing:** Read complete
production partials, entities/configurations, valuation service,
settings provider and UI, and SQL scripts. Identify real route output
enum/stage/status/location rules and how settings are loaded. Record
file/line evidence.

**Gate B --- model contract:** Define draft mode snapshot policy, source
location rule, and legacy null-stage handling. Stop for decision if
current schema cannot safely distinguish stocked WIP from handoff/FG.

**Gate C --- minimal additive code:** Catalogue setting; optional draft
policy snapshot; shared-only resolver; single `InternalRouteWip` policy
branch; read-only source inquiry.

**Gate D --- valuation and rollback:** Reuse existing
genealogy/valuation and test cross-WO dependencies. Patch only verified
gaps.

**Gate E --- SQL Server integration tests:** Run on SQL Server provider,
not only EF InMemory/SQLite. Verify lock/concurrency, rowversion, V2
ledger and rollback.

**Gate F --- regression:** Strict baseline diff and full production test
suite. No existing behavior regression.

## 11. Test cases --- release blockers

1.  Missing/invalid policy → STRICT.
2.  Existing drafts and historical documents → STRICT unless explicitly
    recorded otherwise.
3.  STRICT same WO/route-step source unchanged.
4.  STRICT rejects other WO WIP.
5.  SHARED accepts eligible other-WO stocked WIP.
6.  SHARED can consume from multiple WIP lots and source WOs.
7.  SHARED rejects wrong ItemCode.
8.  SHARED rejects previous-process handoff WIP
    (`ProducingRouteStepId == null`).
9.  SHARED rejects FG staging and non-stocked output.
10. SHARED rejects other company/branch.
11. SHARED respects approved production-location scope.
12. SHARED rejects incompatible base UOM.
13. SHARED rejects future-dated source lot.
14. SHARED rejects blocked/unverified/unvalued pool.
15. SHARED handles multiple material lines drawing same lot without
    overdraw.
16. SHARED handles concurrent consumers without negative balance.
17. Pooled lot with multiple producer receipts: quantity genealogy
    correct, pooled cost correctly labelled.
18. Partial and final depletion cost exact, no stranded cost.
19. Absorbed conversion cost included and V2 output valuation
    reconciles.
20. Consumer rollback restores exact original lot and cost.
21. Source producer rollback blocked while cross-WO dependent active.
22. Source producer rollback permitted after dependent reversal if all
    original rules pass.
23. Flip SHARED→STRICT: historical inquiry and rollback still work.
24. Work Order lifecycle changes cannot orphan active cross-WO
    dependents.
25. Previous-process handoff and manual Material Issue regression tests
    pass.
26. V2 stock ledger posting, valuation evidence and reconciliation pass.
27. Build succeeds; full production tests pass; no new unhandled
    concurrency exceptions.

## 12. Acceptance and final rating

**Architecture/design: APPROVED (repository-grounded).**\
**Implementation readiness: CONDITIONALLY APPROVED --- must clear Gates
A--F.**\
**Production deployment: NOT APPROVED until SQL Server integration and
costing/rollback tests pass.**

The actual repository already provides pooled cost evidence, receipt
contribution allocations and dependency tracking, so this feature should
be significantly smaller and safer than the original plan assumed. The
remaining blockers are **confirmed source eligibility/status/location
semantics**, **draft policy stability**, and **test proof of cross-WO
cost/rollback integrity**.

Do not award an unconditional 10/10 or deploy until those concrete tests
pass.

### Hard stop instructions for coding agent

If Shared mode requires bypassing the current V2 verified valuation
invariant, cannot distinguish WIP from handoff/FG staging, cannot
identify valid source location, or permits source-producer rollback
while downstream consumption remains active: **STOP. Do not deploy.
Report exact file/line and failing test.**
