# Daily Production Work Centre & Process Sequence Enforcement Plan

## 1. Objective

Enforce production routing sequence in **Daily Production** so operators cannot start a later Work Centre/stage or later Process before the required lower sequence has completed.

This plan targets the current Blazor Server ERP repository:

- Repository: `mokth/net10projectTemplate`
- Branch: `production`
- Module: Production / Daily Production

The released **Work Order execution snapshot** is the source of truth.

Use:

- `ProductionWorkOrderRouteStep.StageSequence` for Work Centre / route-stage sequencing.
- `ProductionWorkOrderOperation.ProcessSequence` for Process sequencing inside a route step.

Do **not** implement the new rule from the legacy flattened `ProductionWorkOrderOperation.SequenceNo`.
Do **not** use `PrSchWCenter.SeqNo` / `PrSchProcess.SeqNo` as the runtime authority when the released Work Order snapshot exists.

---

## 2. Required Business Rule

Production must execute in ascending sequence.

### 2.1 Work Centre / route-stage rule

For a Work Order:

1. Find the selected operation's `RouteStep.StageSequence`.
2. Any route step with a **lower StageSequence** is a predecessor stage.
3. A higher stage cannot start until the required predecessor stage is complete.
4. Route steps having the **same StageSequence** are parallel branches and may start simultaneously.
5. Parallel route steps at the same sequence must not block one another merely because one has not started or completed.

Example:

| StageSequence | Work Centre | Behaviour |
|---:|---|---|
| 1 | WC-A | Can start |
| 1 | WC-B | Can start in parallel with WC-A |
| 2 | WC-C | Block until Stage 1 is complete |
| 3 | WC-D | Block until the required earlier stage is complete |

For stage advancement, completion of a sequence group means **all route steps/operations that belong to the required predecessor sequence group have completed their required production**.

### 2.2 Process rule inside a Work Centre

Within the selected route step:

1. Find the selected operation's `ProcessSequence`.
2. Processes with a lower sequence are predecessors.
3. A higher ProcessSequence cannot start until the required previous process sequence group is complete.
4. Processes having the **same ProcessSequence** are parallel and may run simultaneously.
5. All processes in the required lower sequence group must complete before the next process sequence group opens.

Example:

| ProcessSequence | Process | Behaviour |
|---:|---|---|
| 1 | CUT-A | Can start |
| 1 | CUT-B | Can start in parallel |
| 2 | INSPECT | Block until CUT-A and CUT-B complete |
| 3 | PACK | Block until Process Seq 2 completes |

### 2.3 Combined routing rule

Execution hierarchy:

```text
Work Order
  StageSequence 1
    Work Centre A
      ProcessSequence 1
      ProcessSequence 2

    Work Centre B             <- parallel StageSequence 1
      ProcessSequence 1

  StageSequence 2             <- waits for StageSequence 1 group
    Work Centre C
      ProcessSequence 1
      ProcessSequence 2

  StageSequence 3
    Work Centre D
      ProcessSequence 1
```

Equal sequence = parallel.

Higher sequence = dependency.

---

## 3. Important Completion Definition

Do not unlock the next sequence merely because:

- a Daily Production draft exists;
- a Daily Production document was saved;
- an operation has started;
- some quantity was produced.

Sequence completion must be based on **posted production execution facts/projections**.

The current operation snapshot already contains execution projections such as:

- `PlannedOutputQty`
- `GoodQty`
- `ScrapQty`
- `RejectQty`
- `HoldQty`
- `RemainingQty`

For the initial implementation, an operation should be considered sequence-complete when its posted **GoodQty has satisfied its planned output quantity**, using the ERP quantity rounding rules.

Conceptually:

```csharp
IvQty.Round(operation.PlannedOutputQty - operation.GoodQty) <= 0m
```

Centralize this rule instead of duplicating the expression throughout Daily Production.

Do not count an unposted Daily Production draft toward completion.

### 3.1 Scrap/reject/hold

Do not automatically treat Scrap, Reject or Hold as completed Good output.

If the company later needs quantity-loss/yield policies that permit closing an operation below planned GoodQty, that should be a separate controlled enhancement.

The current sequence gate should follow the current executable quantity model and avoid silently redefining production completion.

---

## 4. Immediate Predecessor Group Rule

Use the **immediate lower distinct sequence group**, not a hard-coded `current - 1`.

Example:

```text
StageSequence:
10
20
40
```

For Stage 40, its immediate predecessor sequence is 20.

Likewise:

```text
ProcessSequence:
10
10
30
```

Process 30 waits for **all ProcessSequence 10 siblings**.

Algorithm:

```text
lower = rows where Sequence < currentSequence

if lower is empty:
    current row is sequence-eligible

previousSequence = MAX(lower.Sequence)

previousGroup = rows where Sequence == previousSequence

eligible = every required row in previousGroup is complete
```

This supports gaps in sequence numbering and equal-sequence parallel execution.

### Defensive rule

Although checking the immediate predecessor is normally sufficient because the predecessor itself should only have become executable after its predecessor completed, the server-side implementation should preferably verify **all lower sequence groups are complete**.

This protects the system from:

- migrated/legacy data;
- manual database corrections;
- old records created before the gate existed;
- inconsistent execution projections.

The blocking message can still identify the earliest incomplete predecessor group.

---

## 5. New Central Sequence Gate

Add a dedicated production-domain service/helper, for example:

```text
ErpWeb.Core/Production/ProductionOperationSequenceGate.cs
```

Recommended responsibility:

```csharp
ProductionOperationSequenceGate
```

It should be the single source of truth for Daily Production execution sequencing.

Suggested result model:

```csharp
public sealed record ProductionSequenceGateResult
{
    public bool Allowed { get; init; }

    public string? BlockingLevel { get; init; }
    // STAGE | PROCESS

    public int? BlockingSequence { get; init; }

    public string? BlockingWorkCentreCode { get; init; }

    public string? BlockingOperationCode { get; init; }

    public string? Message { get; init; }
}
```

Suggested API concept:

```csharp
Evaluate(
    ProductionWorkOrderOperation selectedOperation,
    IReadOnlyCollection<ProductionWorkOrderRouteStep> routeSteps,
    IReadOnlyCollection<ProductionWorkOrderOperation> operations)
```

or an async DB-backed equivalent if that fits the existing service architecture better.

Keep the actual rule centralized even if query-side filtering requires an EF-compatible form.

---

## 6. Gate Evaluation Order

For the selected operation:

### Step 1 — Validate graph

Require:

- Work Order exists.
- RouteStep exists.
- `StageSequence` is valid.
- `ProcessSequence` is valid.
- Operation belongs to the selected Work Order and route step.

Fail closed for malformed released snapshots.

### Step 2 — Check Work Centre/stage predecessors

Get all route steps for the Work Order with:

```text
StageSequence < selected.RouteStep.StageSequence
```

Group by `StageSequence`.

If any required lower sequence group is incomplete:

```text
BLOCK
```

Return the earliest useful blocking stage/work centre details.

Equal StageSequence route steps are excluded from predecessor blocking.

### Step 3 — Check Process predecessors

Within the **same RouteStep only**, get operations where:

```text
ProcessSequence < selected.ProcessSequence
```

If any required lower process group is incomplete:

```text
BLOCK
```

Equal ProcessSequence operations are excluded from predecessor blocking.

### Step 4 — Allow

If both checks pass:

```text
ALLOW
```

---

## 7. Stage Completion Calculation

A Work Centre/route step is complete only when all required operations belonging to that route step are complete.

Conceptually:

```text
RouteStepComplete =
    every required operation in RouteStep
        satisfies OperationComplete
```

A stage-sequence group is complete when every route step in that sequence group is complete:

```text
StageGroupComplete =
    every RouteStep where StageSequence == predecessorSequence
        satisfies RouteStepComplete
```

This is important for parallel Work Centres.

Example:

```text
Stage 1:
  WC-A = complete
  WC-B = incomplete

Stage 2:
  WC-C
```

WC-C remains blocked.

Only after both WC-A and WC-B complete does Stage 2 become executable.

---

## 8. Process Completion Calculation

Within one route step:

```text
ProcessGroupComplete =
    every operation where ProcessSequence == predecessorSequence
        satisfies OperationComplete
```

Example:

```text
WC-A

Seq 10:
  CUT-A = complete
  CUT-B = incomplete

Seq 20:
  INSPECT
```

INSPECT remains blocked until both CUT-A and CUT-B complete.

---

## 9. Daily Production Search Eligibility

Modify:

```text
ErpWeb.Core/Production/ProductionOutputService.Entry.cs
```

Current:

```csharp
EligibleOperationsQuery(...)
```

currently filters primarily by:

- valid Work Order status;
- released/current snapshot;
- output configuration;
- supported yield;
- remaining Good quantity.

It does **not** enforce routing predecessor completion.

### Required change

Daily Production search should expose only operations that are sequence-eligible.

Therefore:

```text
Base eligibility
    +
Stage sequence eligibility
    +
Process sequence eligibility
```

must determine whether a new Daily Production operation can be selected.

### Important

Do not simply add:

```csharp
OrderBy(StageSequence)
ThenBy(ProcessSequence)
```

Sorting does not enforce the rule.

An operation must be removed/disabled from actionable search results if its predecessor requirements are not met.

### Recommended UX

Prefer returning blocked operations only if the UI can clearly identify them as blocked.

If keeping the current "eligible operations" semantics, exclude blocked operations from actionable results.

A future enhancement may expose:

```text
Blocked — waiting for WC-A / Stage 10
```

but this is not required for the correctness fix.

---

## 10. Search Ordering

Improve search ordering so executable operations follow routing order.

Recommended:

```text
WorkOrderNo
StageSequence
WorkCentreCode
ProcessSequence
Operation UID
```

Current search ordering primarily uses Work Order + ProcessSequence.

That can visually mix processes from different Work Centres.

Add StageSequence to the result DTO if necessary for deterministic ordering and troubleshooting.

Recommended additions to `ProductionEligibleOperationRow`:

```csharp
public int StageSequence { get; init; }
public int ProcessSequence { get; init; }
```

These may later also be shown in the UI.

---

## 11. GetWorkspaceAsync Enforcement

Modify:

```text
ProductionOutputService.Entry.cs
GetWorkspaceAsync(...)
```

When opening a **new** Daily Production workspace directly by operation ID, evaluate the sequence gate again.

This prevents bypassing search by navigating directly to something like:

```text
/planning/daily-production/new/{operationId}
```

or any equivalent direct call.

If blocked, return a validation result such as:

```text
Cannot start WC-C / ASSY.
Work Centre sequence 20 is waiting for Stage 10 to complete.
```

or:

```text
Cannot start Process PACK (Seq 30).
Process sequence 20 in WC-A must complete first.
```

---

## 12. CreateAsync — Mandatory Server-Side Enforcement

Modify:

```text
ErpWeb.Core/Production/ProductionOutputService.cs
CreateAsync(...)
```

This is the critical enforcement point.

After loading the Work Order operation graph and validating the normal Work Order/snapshot conditions, but before inserting `ProductionOutput`:

```text
Reload/evaluate execution sequence
    ↓
Check stage predecessors
    ↓
Check process predecessors
    ↓
BLOCK or CONTINUE
```

Never rely solely on search eligibility or the Blazor UI.

### Why

Scenario:

1. User A opens Process Seq 20.
2. State changes.
3. User A keeps an old browser workspace.
4. User clicks Save.

`CreateAsync()` must independently decide whether the operation is still executable.

---

## 13. PostAsync — Revalidate Before Posting

Sequence protection should also exist at posting.

Reason:

A draft may be created while valid but remain unposted for some time. Rollbacks or production corrections may subsequently make its predecessor state invalid.

Before posting a Daily Production document:

```text
re-evaluate sequence gate
```

If predecessor requirements are no longer satisfied:

```text
BLOCK POST
```

This makes the posting boundary authoritative.

Do not mutate execution projections before this validation succeeds.

---

## 14. UpdateAsync Behaviour

A saved Daily Production document already has immutable operation identity.

The current rule that prevents changing the operation after creation should remain.

For updates to a `NEW` draft:

- allow editing the document;
- do not treat the draft as production completion;
- sequence must be checked again at Post.

Optionally also revalidate sequence during Update for earlier feedback, but Post remains mandatory.

---

## 15. Posting and Execution Projection Integrity

Inspect:

```text
ErpWeb.Core/Production/ProductionOutputService.Posting.cs
```

Confirm that only successfully posted Daily Production updates:

```text
ProductionWorkOrderOperation.GoodQty
ScrapQty
RejectQty
HoldQty
RemainingQty
```

The sequence gate must read the same authoritative projections/facts.

Avoid calculating predecessor completion from arbitrary Daily Production document sums in one place and operation projections in another.

There should be one consistent execution state.

---

## 16. Rollback Behaviour

Inspect:

```text
ErpWeb.Core/Production/ProductionOutputService.Rollback.cs
```

When posted Daily Production is rolled back:

1. reverse its production effects;
2. recalculate/reverse operation projections;
3. predecessor completion may become false again.

Example:

```text
Process 10:
Planned = 100
Posted Good = 100
=> Process 20 opens

Rollback 20 units from Process 10:
Good = 80
=> Process 10 incomplete
```

After rollback, **new** Process 20 production must again be blocked.

### Downstream rollback safety

There is an additional integrity issue:

If Process 20 or a later Work Centre already has posted production, rolling back Process 10 below its completion threshold could create an impossible route history.

Therefore add/verify rollback protection:

```text
Do not allow rollback of predecessor production if doing so would make the predecessor incomplete while posted downstream production exists.
```

Return a clear message:

```text
Cannot roll back DP00000123 because downstream production has already been posted for Process PACK / Stage 20.
```

This should be part of this implementation because otherwise sequence integrity can be broken after valid production has occurred.

---

## 17. Process Handoff Compatibility

Existing:

```text
ErpWeb.Core/Production/ProductionProcessHandoff.cs
```

already understands lower/higher `ProcessSequence` for same-route-step WIP handoff.

Do **not** replace handoff logic with the new sequence gate.

Responsibilities should remain separate:

### Sequence Gate

Answers:

```text
May this operation execute now?
```

### Process Handoff

Answers:

```text
Which previous process provides the WIP quantity/lot consumed by this process?
```

Both must use compatible sequence semantics.

### Parallel process issue

Current handoff logic can reject ambiguous parallel immediate prior/next process groups.

Do not weaken this safeguard merely to support execution parallelism.

Equal `ProcessSequence` means execution may be parallel, but material handoff between parallel branches requires an unambiguous producer/consumer relationship.

If the existing snapshot cannot determine that relationship, fail safely rather than guessing.

---

## 18. Internal Route WIP Compatibility

Daily Production already handles:

```text
PrMaterialSupplySources.InternalRouteWip
```

and production balance lots.

The stage gate should complement this.

A later Work Centre must not become executable merely because some WIP happens to exist if the business routing says the previous StageSequence group must complete first.

Therefore:

```text
WIP availability != sequence completion
```

Both conditions may be required:

```text
Sequence gate passes
AND
required material/WIP availability passes
```

Do not merge these into one concept.

---

## 19. UI Changes

Files:

```text
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.cs
```

Keep the UI thin.

The UI should consume server eligibility; it should not independently implement route logic.

Recommended display additions to the selected operation summary:

```text
Work Centre: WC-A
Stage: 10

Process: CUT
Process Seq: 20
```

This makes production routing easier for operators to understand.

If a direct selection becomes blocked, display the server message prominently.

Example:

```text
Production cannot start yet.

WC-C (Stage 20) is waiting for Stage 10:
- WC-A — Complete
- WC-B — 80 / 100
```

A compact version is acceptable for the first implementation.

---

## 20. Concurrency Protection

This rule is vulnerable to simultaneous users.

Example:

```text
User A opens later operation.
User B rolls back predecessor production.
User A posts.
```

Therefore sequence validation at posting must occur inside the same transactional boundary used for the production posting mutation.

For SQL Server, use the existing production/inventory locking conventions where applicable.

The important invariant is:

```text
Validate predecessor state
+
post output
+
update execution projections
+
write WIP/stock effects
```

must not permit another transaction to invalidate the routing decision between validation and commit.

Reuse existing transaction/locking infrastructure rather than creating a disconnected locking mechanism.

---

## 21. Error Messages

Use business-readable errors.

### Work Centre blocked

```text
Daily Production cannot start for WC-C (Stage 20).
Previous Stage 10 is not complete.
```

With detail when practical:

```text
Waiting for WC-B: 80 of 100 completed.
```

### Process blocked

```text
Daily Production cannot start for PACK (Process Seq 30).
Process Seq 20 in WC-C must complete first.
```

### Parallel allowed

No warning is needed simply because another operation has the same sequence.

### Invalid snapshot

```text
The Work Order routing snapshot is incomplete or invalid.
Refresh/re-release the Work Order before recording production.
```

Do not silently guess route order.

---

## 22. Recommended Error Code

If the production service has suitable domain validation codes, reuse them.

Otherwise introduce a stable code such as:

```text
PRODUCTION_SEQUENCE_BLOCKED
```

Optionally distinguish:

```text
PRODUCTION_STAGE_SEQUENCE_BLOCKED
PRODUCTION_PROCESS_SEQUENCE_BLOCKED
```

Stable codes are preferable to UI logic parsing error strings.

---

## 23. Data Model

No new database table should be required.

The required sequence data already exists in the current Work Order snapshot:

```text
ProductionWorkOrderRouteStep.StageSequence
ProductionWorkOrderOperation.ProcessSequence
ProductionWorkOrderOperation.PlannedOutputQty
ProductionWorkOrderOperation.GoodQty
```

Do not add a duplicated `PreviousOperationId` solely for this rule.

The routing dependency is derivable from the sequence groups.

---

## 24. Snapshot Validation

At Work Order release/readiness, strengthen validation so an executable snapshot cannot contain unusable sequence data.

Validate:

- StageSequence is valid/positive according to current domain conventions.
- Every operation belongs to a route step.
- ProcessSequence is valid/positive.
- A route step contains at least one executable operation where required.
- final-process rules remain valid.
- parallel sequence groups are accepted.
- no uniqueness validation incorrectly rejects duplicate StageSequence or ProcessSequence.

The model intentionally permits duplicates for parallel execution.

---

## 25. Exact Files to Review/Modify

Primary:

```text
ErpWeb.Core/Production/ProductionOutputService.Entry.cs
ErpWeb.Core/Production/ProductionOutputService.cs
ErpWeb.Core/Production/ProductionOutputService.Posting.cs
ErpWeb.Core/Production/ProductionOutputService.Rollback.cs
ErpWeb.Core/Production/IProductionOutputService.cs
```

Add:

```text
ErpWeb.Core/Production/ProductionOperationSequenceGate.cs
```

Review for compatibility:

```text
ErpWeb.Core/Production/ProductionProcessHandoff.cs
ErpWeb.Core/Production/WorkOrderReadinessValidator.cs
ErpWeb.Core/Production/WorkOrderSnapshotBuilder.cs
ErpWeb.Model/Entities/Production/ProductionWorkOrderRouteStep.cs
ErpWeb.Model/Entities/Production/ProductionWorkOrderOperation.cs
```

UI:

```text
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.cs
```

Tests:

```text
ErpWeb.Tests/ProductionOutputEntryServiceTests.cs
ErpWeb.Tests/ProductionDailyOutputCoreTests.cs
```

Add a focused suite if cleaner:

```text
ErpWeb.Tests/ProductionOperationSequenceGateTests.cs
```

---

## 26. Required Test Scenarios

Even if the implementation workflow does not require broad unit-test expansion, this routing rule is production-critical and should have focused regression coverage.

### Stage sequencing

#### Case A — first stage

```text
Stage 10 / WC-A
```

Expected:

```text
ALLOWED
```

#### Case B — later stage while previous incomplete

```text
Stage 10 / WC-A = 80 / 100
Stage 20 / WC-B = target
```

Expected:

```text
WC-B BLOCKED
```

#### Case C — previous stage complete

```text
Stage 10 / WC-A = 100 / 100
Stage 20 / WC-B
```

Expected:

```text
WC-B ALLOWED
```

#### Case D — parallel Work Centres

```text
Stage 10 / WC-A
Stage 10 / WC-B
```

Expected:

```text
WC-A ALLOWED
WC-B ALLOWED
```

Neither waits for the other to start.

#### Case E — parallel stage group before next stage

```text
Stage 10 / WC-A = complete
Stage 10 / WC-B = incomplete
Stage 20 / WC-C
```

Expected:

```text
WC-C BLOCKED
```

When WC-B completes:

```text
WC-C ALLOWED
```

### Process sequencing

#### Case F — first process

```text
Process 10
```

Expected:

```text
ALLOWED
```

#### Case G — later process

```text
Process 10 = incomplete
Process 20 = target
```

Expected:

```text
Process 20 BLOCKED
```

#### Case H — previous process complete

Expected:

```text
Process 20 ALLOWED
```

#### Case I — parallel processes

```text
Process 10 / CUT-A
Process 10 / CUT-B
```

Expected:

```text
both ALLOWED
```

#### Case J — next process after parallel group

```text
CUT-A = complete
CUT-B = incomplete
INSPECT Seq 20
```

Expected:

```text
INSPECT BLOCKED
```

Only after both complete:

```text
INSPECT ALLOWED
```

### Sequence gaps

#### Case K

```text
Stage 10 -> Stage 30
Process 10 -> Process 50
```

Expected:

No requirement for sequence 20/40 to exist.

Use the next distinct sequence relationship.

### Security / bypass

#### Case L — direct operation URL

Attempt to open blocked operation directly.

Expected:

```text
BLOCKED
```

#### Case M — direct CreateAsync

Call service directly for blocked operation.

Expected:

```text
BLOCKED
```

#### Case N — stale draft / posting

Predecessor becomes invalid before posting.

Expected:

```text
POST BLOCKED
```

### Rollback

#### Case O

Rollback predecessor while no downstream production exists.

Expected:

```text
allowed according to normal rollback rules
later sequence closes again
```

#### Case P

Rollback predecessor below completion while downstream posted production exists.

Expected:

```text
ROLLBACK BLOCKED
```

---

## 27. Acceptance Criteria

Implementation is approved when all of the following are true:

1. First StageSequence can start normally.
2. Same StageSequence Work Centres can start in parallel.
3. Higher StageSequence cannot start while a required lower sequence group is incomplete.
4. All parallel Work Centres in the predecessor sequence group must complete before the next stage opens.
5. First ProcessSequence inside an eligible Work Centre can start.
6. Same ProcessSequence operations can start in parallel.
7. Higher ProcessSequence cannot start until the required lower process sequence group is complete.
8. All parallel processes in the predecessor group must complete before the next process sequence opens.
9. Sequence gaps are supported.
10. Draft Daily Production does not count as completion.
11. Only posted production facts/projections unlock downstream execution.
12. Search eligibility respects sequence.
13. Direct workspace access cannot bypass sequence.
14. `CreateAsync()` cannot bypass sequence.
15. Posting revalidates sequence.
16. Rollback cannot create an invalid downstream production history.
17. Internal WIP availability remains a separate material condition.
18. Existing `ProductionProcessHandoff` behaviour is not weakened.
19. Tenant/company/branch isolation remains intact.
20. Existing Work Order release/in-progress rules remain intact.
21. Existing quantity, material, stock, future-stock and posting controls remain intact.
22. No database migration is introduced unless implementation discovers an actual schema deficiency.
23. UI remains a consumer of server rules rather than the authority.

---

## 28. Implementation Order

### Phase 1 — Domain rule

1. Add `ProductionOperationSequenceGate`.
2. Centralize operation completion calculation.
3. Implement StageSequence grouping.
4. Implement ProcessSequence grouping.
5. Add focused tests.

### Phase 2 — Read/selection enforcement

6. Integrate with Daily Production eligible-operation search.
7. Integrate with `GetWorkspaceAsync`.
8. Improve result ordering by StageSequence + ProcessSequence.
9. Expose sequence values in DTO/UI where useful.

### Phase 3 — Write enforcement

10. Integrate with `CreateAsync`.
11. Revalidate inside posting transaction.
12. Verify operation projections are updated only from posted facts.

### Phase 4 — Rollback integrity

13. Re-evaluate sequence state after rollback.
14. Block predecessor rollback when posted downstream execution would become invalid.

### Phase 5 — Compatibility

15. Verify `ProductionProcessHandoff`.
16. Verify internal route WIP.
17. Verify Work Order readiness/snapshot validation.
18. Verify parallel route/process definitions.
19. Run production regression tests.

---

## 29. Implementation Guidance for Coding Agent

The coding agent should **not** solve this by merely sorting the Daily Production list.

The invariant is:

```text
An operation is executable only when:

1. its Work Order is execution-eligible;
2. all required lower Work Centre StageSequence groups are complete;
3. all required lower ProcessSequence groups inside its route step are complete;
4. its material/WIP and other existing production rules pass.
```

And:

```text
Same StageSequence = parallel Work Centres.
Same ProcessSequence = parallel Processes.
```

The authoritative enforcement boundary is server-side.

At minimum, revalidate during:

```text
GetWorkspaceAsync
CreateAsync
PostAsync
```

and use the same rule to constrain:

```text
SearchEligibleOperationsAsync
```

Never infer completion from an unposted document.

Never guess an ambiguous process-handoff relationship.

Never allow rollback to leave already-posted downstream production without a valid predecessor history.

---

## 30. Final Target Behaviour

Given:

```text
WO-001

Stage 10
  WC-CUT
    Process 10 CUT-A
    Process 10 CUT-B
    Process 20 QC

Stage 10
  WC-MOULD
    Process 10 MOULD

Stage 20
  WC-ASSEMBLY
    Process 10 ASSY
    Process 20 PACK
```

Initial state:

```text
CUT-A   -> allowed
CUT-B   -> allowed
MOULD   -> allowed

QC      -> blocked
ASSY    -> blocked
PACK    -> blocked
```

After CUT-A + CUT-B complete:

```text
QC      -> allowed
```

While MOULD remains incomplete:

```text
ASSY    -> blocked
```

After QC and MOULD complete:

```text
ASSY    -> allowed
```

After ASSY completes:

```text
PACK    -> allowed
```

That is the intended Daily Production execution behaviour.

---

## 31. Recommendation

Implement this before expanding Daily Production usage.

The current Work Order model already contains the correct two-level sequence structure:

```text
RouteStep.StageSequence
Operation.ProcessSequence
```

so the main missing piece is a centralized, transaction-safe **execution sequence gate** and its integration into Daily Production search, workspace selection, creation, posting and rollback.

This should be treated as a production-integrity rule, not a UI convenience.

---

# 10/10 Repository-Specific Resolution Addendum

This addendum resolves the remaining implementation ambiguities after re-checking the current `production` branch. These decisions override less-specific wording earlier in the plan.

## 1. Posting transaction and lock point

`ProductionOutputService.Posting.cs` already opens one transaction and locks the ProductionOutput, posting link, Work Order, RouteStep and Operation. SQL Server helpers use `UPDLOCK, HOLDLOCK`.

Do **not** add another transaction.

Run the sequence gate in `PostAsync()` immediately after the Work Order, selected RouteStep and selected Operation are locked and basic snapshot checks pass, but **before** process handoff, material/lot consumption, WIP/stock mutation or execution projection mutation.

Load sequence-dependent route/operation rows under the same transaction. On SQL Server use `UPDLOCK, HOLDLOCK` and deterministic ordering:

```text
RouteStep: StageSequence, UID
Operation: RouteStepId, ProcessSequence, UID
```

The Work Order is the serialization root. Daily Production posting and rollback for the same WO must acquire the Work Order lock before sequence-dependent child state. Required logical lock order:

```text
ProductionOutput
PostingLink
WorkOrder
RouteSteps / Operations
Materials / production lots / inventory resources
```

This prevents a predecessor rollback and downstream post from both committing an invalid routing state.

## 2. Search eligibility — filter in SQL

Do not load every candidate graph and evaluate it in memory.

Extend `EligibleOperationsQuery(...)` with SQL-translatable `NOT EXISTS` predicates **before Count/Skip/Take**.

A candidate is stage-eligible only when there is no incomplete operation in any lower StageSequence of the same Work Order:

```text
NOT EXISTS lower route/operation
WHERE lowerRoute.WorkOrderId = candidate.WorkOrderId
  AND lowerRoute.StageSequence < candidate.RouteStep.StageSequence
  AND lowerOperation is incomplete
```

A candidate is process-eligible only when there is no incomplete operation in the same RouteStep with lower ProcessSequence:

```text
NOT EXISTS priorOperation
WHERE priorOperation.RouteStepId = candidate.RouteStepId
  AND priorOperation.ProcessSequence < candidate.ProcessSequence
  AND priorOperation is incomplete
```

Equal StageSequence and equal ProcessSequence are excluded and therefore remain parallel.

Because the defensive rule requires **all lower sequence groups complete**, checking all lower groups is intentional and avoids an extra MAX-sequence calculation.

Never fetch 20 candidates and then remove blocked rows in memory; that breaks page size and TotalCount.

Add `StageSequence` and `ProcessSequence` to `ProductionEligibleOperationRow` and order search results by:

```text
WorkOrderNo
StageSequence
WorkCentreCode
ProcessSequence
UID
```

Only add indexes after inspecting existing index coverage/query plans. Useful shapes, if absent, are `(WorkOrderId, StageSequence, UID)` on route steps and `(WorkOrderId, RouteStepId, ProcessSequence, UID)` on operations.

## 3. Exact completion rule

Centralize:

```csharp
IsOperationComplete =
    IvQty.Round(PlannedOutputQty - GoodQty) <= 0m;
```

For this milestone, **GoodQty only** determines sequence completion.

Do not use ProcessedQty, Scrap, Reject, Hold, draft quantity, WIP existence or document existence.

Current posting updates `operation.GoodQty` from posted facts and rollback subtracts it, so this projection is the correct gate source.

## 4. GetWorkspace and Create

`GetWorkspaceAsync(operationId)` must reject a blocked operation even if reached directly.

`CreateAsync()` must also evaluate the sequence rule for early feedback. Draft creation does not change execution projections, so it does not need the heavy posting lock strategy.

A previously valid draft may remain viewable/editable if predecessor state later changes, but `PostAsync()` must reject it until the gate passes again.

## 5. Exact rollback downstream guard

Current `RollbackAsync()` already runs in one transaction and locks WorkOrder, RouteStep and Operation.

Add the guard **before writing any reversal movement or mutating lots**.

Calculate:

```csharp
nextOperationGood = IvQty.Round(operation.GoodQty - output.GoodQty);

wasComplete  = IsOperationComplete(operation.PlannedOutputQty, operation.GoodQty);
willComplete = IsOperationComplete(operation.PlannedOutputQty, nextOperationGood);
```

Only run the downstream-history block when:

```text
wasComplete && !willComplete
```

Downstream means:

```text
A. same RouteStep and ProcessSequence > current ProcessSequence

OR

B. same WorkOrder and RouteStep.StageSequence > current StageSequence
```

Equal process/stage sequences are parallel siblings and are **not** downstream.

Effective downstream activity is:

```text
ProductionOutput.Status == POSTED
```

for one of those downstream operations.

Do not count `NEW` or `REVERSED`.

Use POSTED output rather than downstream GoodQty alone because a posted downstream record containing Scrap/Reject/Hold is still real execution that depended on the predecessor being valid.

If effective downstream execution exists, block rollback. Return the earliest useful downstream document ordered by StageSequence, ProcessSequence, ProductionDate and output UID, with a message such as:

```text
Cannot roll back DP00000123 because downstream production
DP00000130 is already POSTED for WC-ASSEMBLY / ASSY.
```

Because both Post and Rollback acquire the Work Order lock before sequence-dependent mutation, this downstream check remains valid through commit.

## 6. Parallel process vs process handoff

The sequence gate allows equal `ProcessSequence` processes to execute simultaneously.

Do **not** weaken `ProductionProcessHandoff.TryGetImmediatePrior/TryGetImmediateNext`. It currently rejects ambiguous immediate parallel producer/consumer groups, which is correct.

Therefore:

```text
Parallel execution = supported.
Ambiguous automatic WIP handoff between parallel branches = fail safely.
```

An explicit producer/consumer relationship for parallel branches is a separate future enhancement.

## 7. Required focused tests

Add/extend tests proving:

1. Later stage excluded while any lower-stage operation is incomplete.
2. Equal StageSequence route steps are simultaneously eligible.
3. Later process excluded while lower process is incomplete.
4. Equal ProcessSequence processes are simultaneously eligible.
5. SQL-side filtering preserves correct TotalCount and paging.
6. Direct `GetWorkspaceAsync` rejects blocked operation.
7. Direct `CreateAsync` rejects blocked operation.
8. `PostAsync` rejects a stale draft after predecessor becomes incomplete.
9. Post succeeds after all lower predecessor groups complete.
10. Rollback that leaves predecessor complete succeeds.
11. Complete→incomplete rollback succeeds with no downstream POSTED output.
12. Same rollback is blocked by later-process POSTED output.
13. Same rollback is blocked by later-stage POSTED output.
14. Downstream NEW draft does not block rollback.
15. Downstream REVERSED output does not block rollback.
16. Equal-sequence parallel sibling does not count as downstream.
17. Sequence gaps such as 10→30 work.
18. Tenant/branch isolation remains intact.
19. SQL Server concurrency test proves predecessor rollback and downstream post cannot both commit an invalid state.

Use the repository's SQL Server production concurrency-test pattern for item 19 because the guarantee depends on `UPDLOCK, HOLDLOCK`.

## 8. Exact implementation order

1. Add `ProductionOperationSequenceGate`.
2. Centralize `IsOperationComplete`.
3. Add SQL-translatable stage/process predecessor predicates to `EligibleOperationsQuery`.
4. Add StageSequence/ProcessSequence to the eligible-operation DTO and ordering.
5. Apply gate to `GetWorkspaceAsync`.
6. Apply gate to `CreateAsync`.
7. In `PostAsync`, evaluate under the existing Work Order-rooted transaction/locks before any production mutation.
8. In `RollbackAsync`, add complete→incomplete downstream POSTED-output protection before reversal mutation.
9. Preserve process-handoff ambiguity protection.
10. Run focused service tests and SQL Server concurrency regression.

# Final Approval

**Score: 10/10 — APPROVED FOR IMPLEMENTATION.**

The three previously open areas are now resolved against the current repository:

- **Posting concurrency:** existing transaction + Work Order-rooted `UPDLOCK, HOLDLOCK` serialization.
- **Search performance:** SQL-side `NOT EXISTS` predecessor filtering before Count/Skip/Take; no N+1 graph evaluation.
- **Rollback integrity:** complete→incomplete check plus effective downstream `POSTED ProductionOutput` detection, excluding NEW, REVERSED and equal-sequence parallel siblings.

The coding agent should implement these decisions as invariants rather than choose alternative behavior during implementation.
