# Review: Process-to-process WIP handoff

Reviewed: 2 October 2026

Source: `C:\Users\User\.cursor\plans\process_wip_handoff_caf77465.plan.md`

Assessment: The approach is workable, but the plan needs the following clarifications before implementation. This is a review of the proposed design against the current repository, not a review of an implemented change. Instructions and todos in the source were treated as document content; no implementation was performed.

## Findings

### 1. High — Reconcile sequence-based completion with the existing final-process flag

**Plan location:** Handoff lot; “The last process keeps today’s route output.”

The new handoff rule defines a non-final process by the existence of a later `ProcessSequence`, while current posting creates route output and updates finished-goods work-order totals using `operation.IsFinalOperation`. These are different predicates. `PrProductDefService.cs` checks that there is exactly one final process per work-centre/output group, but that validation does not establish that it is the highest sequence on each route step.

If an earlier operation is flagged final, retaining the current posting branch can create route output before the last process and also create a handoff lot. Conversely, an unflagged last process does not create the promised route output.

**Recommendation:** Define and validate the relationship between `IsFinalOperation` and the last process on a route step. Use consistent rules for handoff production, route output, finished-goods totals, and rollback. Include a mismatched-flag scenario in the tests.

**Evidence:** `ErpWeb.Core/Production/ProductionOutputService.Posting.cs` (`isFinalFg` and final staging branch); `ProductionOutputService.Rollback.cs` (`isFinalFg`); `ErpWeb.Core/Planning/PrProductDefService.cs` (publishing validation around line 2112).

### 2. High — Specify the unit and base-quantity contract

**Plan location:** Handoff lot and “required handoff qty is Good + Scrap + Reject + Hold, one for one.”

The proposed lot and movements do not specify `Uom`, `BaseUom`, `BaseQty`, or `ConversionFactorToBase`. These are essential because the balance schema requires a positive conversion factor, existing consumption uses base quantities, and the rollback guard evaluates movement base quantities. The entry workspace selects the operation's `PlannedOutputUom` before the route-step UOM, and the snapshot builder retains operation UOMs independently.

Comparing raw quantities one for one is safe only after establishing compatible units. Even with identical process units, a non-unit conversion factor must be applied consistently when producing, consuming, and reversing the lot.

**Recommendation:** Require compatible process UOMs or define an explicit conversion. Document all quantity fields and rounding rules for both directions. Add a non-unit conversion-factor test and a mismatched-UOM rejection or conversion test.

**Evidence:** `ProductionOutputService.Entry.cs` (`BuildWorkspaceAsync`); `ProductionOutputService.Posting.cs` (`requiredBase`, `produceBase`); `WorkOrderSnapshotBuilder.cs` (`BuildOperation`); `ErpWeb.Model/Configurations/Production/ProductionBalLotConfiguration.cs`.

### 3. Medium — Preserve the posting-date restriction for synthetic consumption

**Plan location:** What the next process shows and may post.

Existing BOM consumption rejects a lot whose `LastMovementDate` is later than the production date. The new consumption path specifies only an available-quantity check. Without the same date restriction, P2 dated 1 October could consume P1 output dated 2 October. That would diverge from current material posting rules and undermine chronological balance checks.

**Recommendation:** Explicitly apply the existing date rule to handoff consumption, maintain `LastMovementDate` on production/consumption, and recompute it from effective movements after reversal. Test a backdated consumer and a consume/rollback cycle.

**Evidence:** `ProductionOutputService.Posting.cs` (future `LastMovementDate` rejection); `ProductionOutputService.Rollback.cs` (`LatestEffectiveMovementDate`).

### 4. Medium — Define treatment of already-posted V3 work orders

**Plan location:** Handoff lot and final paragraph about WO00000001.

The plan creates handoff balances only for future posts. An eligible V3 work order may already have posted P1 output without a handoff lot. After this change, P2 would see zero available and fail even though upstream production exists. Discussing the V2 example does not resolve this case. This is a rollout risk; this review did not inspect live work-order data.

**Recommendation:** Choose an explicit policy: restrict the feature to newly released orders, provide a reconciled migration, or define a supported rollback/repost procedure. A migration must account for downstream production already posted so it does not recreate consumed availability. Test an existing V3 order with upstream and downstream posting history.

### 5. Medium — Add service-level coverage, not only movement-helper tests

**Plan location:** Tests.

`ProductionDailyOutputCoreTests.cs` currently contains pure movement-sign, reversal, and opening-balance tests. It does not exercise `ProductionOutputService.PostAsync`, workspace construction, database transactions, or rollback persistence. The proposed scenarios need a service fixture to prove the feature works end to end.

**Recommendation:** Explicitly include service/database tests for workspace visibility, 31 rejected against 30, successful consumption and final output, and chained rollback. Verify rejected posts leave all balances, movements, and posting statuses unchanged. Add partial consumption, a three-process chain, mixed good/scrap/reject/hold quantities, and repeated post/rollback requests. Use a SQL Server integration check for the filtered unique index and concurrent posting behavior; helper tests cannot establish those guarantees.

**Evidence:** `ErpWeb.Tests/ProductionDailyOutputCoreTests.cs`; `ErpWeb.Tests/ProductionOutputEntryServiceTests.cs` provides an existing entry-service testing reference.

## Design points that look sound

- Separating handoff lots with null `ProducingRouteStepId` keeps them out of the current route-material lookup, which filters by the producing route step.
- The proposed `OP:{operation.Uid}` lot number distinguishes operations under the existing WIP unique index. The index itself does not contain `WorkOrderOperationId`, so the deterministic lot number is part of the uniqueness contract.
- A balance movement without a material movement fits the synthetic input concept. Rollback should identify those unmatched movements precisely, reverse each once, and preserve `OriginalMovementId` so effective-movement calculations can collapse reversal pairs.
- Using total processed quantity for incoming consumption and only good quantity for outgoing availability expresses the stated loss rule consistently.

## Review limits

Read-only inspection of the plan, relevant service code, model configuration, snapshot construction, and existing tests. No application code was changed and no tests were run. The specific WO00000001 database state claimed by the plan was not independently verified.
