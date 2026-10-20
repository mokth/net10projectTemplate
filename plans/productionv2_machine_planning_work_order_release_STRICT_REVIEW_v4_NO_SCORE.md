# productionv2 — Machine Planning / Work Order Release Critical Fix
## STRICT RE-REVIEW v4 — NO SCORE / NO RUBBER-STAMP APPROVAL

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Repository HEAD reviewed:** `551d0f84fe0449ac28b5f93b93300b4d328e632d` — `checked in costing again`  
**Additional evidence reviewed:** `productionv2_machine_schedule_release_blocker_zero_cycle_time_DIAGNOSIS.md`  
**Review status:** **v3 is superseded. This v4 is a corrective implementation roadmap, NOT a claim that the implementation is already safe.**  
**Production status:** **NOT APPROVED FOR PRODUCTION ENABLEMENT.**

There is intentionally **no 10/10 score** in this document. The only valid approval comes after the implementation exists and the mandatory SQL Server/concurrency/regression evidence passes.

---

# 1. Why v3 is not accepted unchanged

A stricter repository review found material omissions and overreach in v3.

## 1.1 v3 treated the zero-duration compatibility filter too simply

A blind filter such as:

```csharp
duration > 0
```

is enough to stop the diagnosed `WO00000002` from blocking other releases, but it is not sufficient as the final rule.

A corrupted active row could have:
- computed duration = 0;
- saved Start < Completion; or
- positive allocation fragments.

Ignoring that row would hide potentially real occupied capacity.

The final implementation must classify zero-duration rows safely rather than simply exclude all of them.

---

## 1.2 v3 missed self-capacity inside the Work Order being scheduled

Current `WorkOrderScheduleCalculator` schedules equal-sequence operations from the same anchor and does not maintain physical-machine occupancy from operations already placed in the same Draft.

Therefore two operations in one WO can select the same physical machine and be scheduled over each other.

The current Release path may then reject them later because it accumulates pending intervals separately.

Fixing only external active allocations would still leave this Draft-vs-itself inconsistency.

---

## 1.3 v3 did not require Draft and Release to use the same deterministic placement order

Current Draft scheduling follows:

```text
StageSequence
  -> route step deterministic order
    -> ProcessSequence
      -> operation deterministic order
```

Current Release flattens selected machines and reconstructs them independently.

After finite-capacity logic is added, different ordering can produce different placements on the same physical machine.

The correct fix is not just a shared "capacity reader"; Draft calculation and Release revalidation must share the same deterministic Work Order placement engine.

---

## 1.4 v3 did not define DbContext / transaction ownership

`ProductionMachineScheduleLock` is a SQL Server transaction-owned `sp_getapplock`.

If a shared capacity service opens a second DbContext/connection during Release, the capacity query is not protected by the caller's machine lock transaction.

For Release, Planner Commit, Reopen and Backfill:
- capacity reads;
- allocation validation;
- allocation writes;
- machine locks

must use the **same caller-owned AppDbContext and SQL transaction**.

A read-only Draft preview may use a separate context because Release will revalidate under locks.

---

## 1.5 v3 overreached by introducing snapshot-hash V5 as part of the critical fix

The current repository deliberately freezes `SnapshotRevision` and `SnapshotHash` during schedule-only Planner moves even though V4 contains schedule fields.

That is an existing architectural compromise.

Changing the hash contract while also fixing machine capacity creates a large migration surface:
- V4 Draft compatibility;
- existing Released/InProgress WOs;
- downstream posting links carrying SnapshotHash;
- refresh behavior.

That should not be bundled into the immediate machine-capacity correction.

However, the hash/integrity gap remains real and is documented later as a separate hardening gate.

---

## 1.6 v3 proposed a simple Reopen hash re-baseline without enough proof

Blindly recomputing V4 on Reopen would be unsafe.

V4 hashes much more than schedule dates:
- BOM/route/resource facts;
- machine cycle data;
- costing inputs;
- production quantities.

If current aggregate data had been tampered with, blind recomputation would bless it.

Therefore this plan does **not** instruct Reopen to simply recompute V4.

---

## 1.7 v3 missed scheduling precision behavior

Current calculation:

```csharp
PlannedRunMinutes =
    Round4(PlannedCycleSlots * CycleSeconds / 60m);
```

Scheduler then converts the rounded minutes back to seconds.

Example:

```text
Cycle = 50 seconds
=> 50 / 60 = 0.833333...
=> rounded to 0.8333 min
=> scheduler uses 49.998 sec
```

This explains millisecond values such as `...39.998` visible in the debug data.

A very small positive CycleSeconds can even round to `PlannedRunMinutes = 0`.

This is not the root cause of `WO00000002` (its CycleSeconds is genuinely zero), but Product Definition validation must respect the duration precision actually used by the current scheduler.

Do not silently change the duration formula in the critical capacity patch without migration/regression evidence.

---

# 2. Confirmed reported failure

The supplied database diagnosis proves:

- New WO: `WO00000004`
- Blocking existing WO: `WO00000002`
- Same physical machine: `M1`
- `WO00000002` status: `IN_PROGRESS`
- machine Start == Completion
- operation Start == Completion
- CycleSeconds = 0
- SetupSeconds = 0
- ConversionSeconds = 0
- QueueSeconds = 0
- PlannedRunMinutes = 0
- no allocation fragments.

The Release error names `WO00000002`, not `WO00000004`.

Therefore the immediate failure occurs while scanning existing machine capacity.

`WO00000004` inherited the same zero-duration Product Definition, so after removing the legacy blocker it would still fail on its own.

Both sides must be fixed.

---

# 3. Business rules that must remain true

## 3.1 Cycle time is not universally mandatory

Keep:

```text
CycleSeconds >= 0
SetupSeconds >= 0
ConversionSeconds >= 0
QueueSeconds >= 0
OutputPerCycle > 0
ParallelMachineCount >= 1
```

Do not implement:

```text
CycleSeconds > 0
```

as a universal rule.

A setup-only machine step may legitimately have:

```text
CycleSeconds = 0
SetupSeconds > 0
```

---

## 3.2 A selected machine operation must produce a positive schedulable duration

Under the **current repository scheduling formula**:

```text
duration =
SetupSeconds
+ ConversionSeconds
+ QueueSeconds
+ PlannedRunMinutes * 60
```

A selected machine WO operation must have:

```text
duration > 0
```

before a schedule is saved or a WO is released.

---

## 3.3 Physical capacity key

Physical machine capacity remains:

```text
CompanyCode + MachineCode
```

not branch-scoped and not product-scoped.

"Same product" is not the business restriction.

Two different products using the same physical machine compete for the same capacity.

---

## 3.4 Half-open interval rule

Keep:

```text
[Start, End)
```

Adjacent intervals are legal:

```text
08:00-10:00
10:00-12:00
```

No artificial gap is introduced.

---

# 4. Phase 0 — tests before implementation

Before changing production code, add failing regressions that reproduce the current defects.

## 4.1 Exact zero-duration blocker

Seed:

```text
WO-A IN_PROGRESS
Machine M1
duration = 0
Start == Completion
no allocations
```

Create a valid positive-duration Draft WO-B on M1.

Current behavior should reproduce the blocker.

The test becomes the acceptance test for the compatibility correction.

---

## 4.2 Exact Planner -> later Release regression

1. Release positive-duration WO-A on M1.
2. Planner-reschedule WO-A.
3. Create WO-B using M1.
4. Recalculate WO-B.
5. Release WO-B.

Current behavior must be captured before changing architecture.

The final expectation is:
- WO-B is placed in real free capacity;
- Release succeeds;
- allocations do not overlap.

---

## 4.3 Same-Draft self-overlap regression

Create one Draft containing two operations that:
- are eligible to execute in parallel by sequence;
- select the same physical machine;
- each has positive duration.

Capture whether current scheduler gives overlapping machine windows.

Final behavior must never commit overlapping allocations for those two operations.

---

# 5. Phase 1 — one canonical schedule-duration helper

Create a small Core helper, e.g.:

```text
ProductionMachineScheduleDuration
```

Its purpose is consistency, not new business logic.

For the critical fix it should preserve the current scheduler contract:

```text
SetupSeconds
+ ConversionSeconds
+ QueueSeconds
+ PlannedRunMinutes * 60
```

Use it in:
- WO readiness;
- WO scheduler;
- Release active-row classification;
- Planner capacity loader;
- Planner readiness;
- Backfill diagnostics.

Do **not** change machine costing formulas.

Do **not** switch the scheduler to exact `PlannedCycleSlots * CycleSeconds` in the same change.

Create a separate follow-up issue for exact-second scheduling precision if desired.

---

# 6. Phase 2 — safe zero-duration active-row classification

Do not make the final implementation just:

```text
WHERE duration > 0
```

Load affected active rows and classify.

## 6.1 `POSITIVE_CAPACITY`

```text
duration > 0
```

Must participate in normal allocation verification.

---

## 6.2 `LEGACY_EMPTY_CAPACITY`

May be ignored as physical occupancy only when all are true:

```text
SetupSeconds >= 0
ConversionSeconds >= 0
QueueSeconds >= 0
PlannedRunMinutes >= 0

duration == 0

MachineStart is not null
MachineCompletion is not null
OperationStart is not null
OperationCompletion is not null

MachineStart == MachineCompletion
OperationStart == MachineStart
OperationCompletion == MachineCompletion

no current positive allocation fragments exist
```

Do not use `duration <= 0` for this class.

Negative values or offsetting negative/positive values are corruption, not legacy zero.

The diagnosed `WO00000002` fits the intended empty-capacity class.

---

## 6.3 `INVALID_CAPACITY_STATE`

Block/fail closed when any of these occur:

- negative duration component;
- duration = 0 but saved Start < Completion;
- duration = 0 but positive allocation fragment exists;
- machine/operation timestamps disagree;
- partial/null timestamp shape cannot prove an empty interval;
- allocation identity points to a different active machine/WO/operation;
- invalid current revision/provenance.

Return an exact diagnostic including:
- WorkOrderNo;
- OperationCode;
- MachineCode;
- failed invariant.

Do not silently skip it.

---

# 7. Phase 3 — Product Definition activation guard

Modify:

`ErpWeb.Core/Planning/PrProductDefService.cs`

Apply the new rule only during **publish/activation**, not while authoring an unfinished Draft definition.

For machine-driven operations:
- MACHINE;
- AUTOMATED;
- PACKING with machine options;

every selectable machine option must be capable of producing positive scheduled duration under the repository's current precision.

Because `PlannedRunMinutes` is rounded to quantity precision, do not check only raw:

```text
CycleSeconds + Setup + Conversion + Queue > 0
```

Instead use a helper equivalent to:

```text
fixedSeconds = Setup + Conversion + Queue

oneSlotRunMinutes = Round4(CycleSeconds / 60)

valid =
    fixedSeconds > 0
    OR oneSlotRunMinutes > 0
```

This avoids activating a machine option whose positive but tiny cycle time always rounds to zero for a one-slot run.

Keep:
- OutputPerCycle > 0;
- ParallelMachineCount > 0;
- non-negative times.

Do not silently deactivate existing ACTIVE definitions.

Provide a diagnostic report/query for existing ACTIVE machine options that violate the new rule.

---

# 8. Phase 4 — Work Order readiness and scheduler guard

## 8.1 Readiness

Add stable readiness code:

```text
WO_MACHINE_DURATION_REQUIRED
```

For a machine-capable operation, evaluate the **selected machine**.

If selected machine duration <= 0:

> Operation {OperationCode} / Machine {MachineCode} has no positive machine duration. Correct the Product Definition and use Update from Product Definition on this Draft.

Do not add a new duration failure merely because an unselected legacy alternate option has zero time.

Keep existing validation for other persisted resource facts unless separately reviewed.

---

## 8.2 WorkOrderScheduleCalculator

Before placing the selected machine:

```text
if duration <= 0
    return WO_MACHINE_DURATION_REQUIRED
```

Do not persist a newly calculated zero-length machine schedule.

The low-level `ProductionCalendarScheduler` may continue supporting `TimeSpan.Zero`; the Work Order business layer must not use that for a selected capacity-consuming machine operation.

---

# 9. Phase 5 — shared capacity service, with explicit context ownership

Create:

```text
IProductionMachineCapacityService
ProductionMachineCapacityService
```

The service is responsible for **capacity evidence**, not route sequencing.

## 9.1 Transactional API

Methods used by:
- Release;
- Planner Commit;
- Reopen;
- Backfill

must accept the caller-owned:

```text
AppDbContext db
```

and must not open a second DbContext for protected capacity reads/writes.

This is mandatory because:
- machine applocks are `LockOwner = Transaction`;
- allocation reads must occur under the same SQL transaction that owns the lock.

## 9.2 Preview/read API

Draft/Recalculate may use a read-only context because:
- Draft is not a reservation;
- Release revalidates under locks.

The API must make this difference explicit rather than hiding context creation.

---

## 9.3 Runtime capacity scope

Capacity checks should be scoped to:
- CompanyCode;
- affected MachineCode(s);
- relevant horizon/window.

Do not make an unrelated bad machine elsewhere in the company block every Work Order Release.

Planner global readiness may remain company-wide.

Runtime Release is affected-machine scoped.

---

# 10. Positive active capacity must become allocation-authoritative

For an active `POSITIVE_CAPACITY` machine row:

authoritative physical occupancy is the current verified:

```text
PrMachineScheduleAllocation
```

Do not reconstruct the active job from raw calendar during normal runtime capacity checks.

Validate:
- WorkOrder active status;
- IsSelected;
- WorkOrderId;
- WorkOrderOperationId;
- WorkOrderMachineId;
- CompanyCode;
- BranchCode;
- MachineCode;
- ScheduleRevision;
- ScheduleSourceHash;
- positive fragment interval.

Missing/current-invalid allocations are blockers.

---

## 10.1 Important exception — controlled backfill

The allocation backfill/migration path may reconstruct an old positive-duration saved schedule from raw calendar to establish initial allocation evidence.

That is a migration/repair operation, not normal Release/Planner capacity logic.

If reconstruction is impossible:
- backfill blocks;
- runtime remains fail-closed.

---

# 11. Do not over-block on inactive allocation debris

Only active Released/InProgress WOs define current occupied machine capacity.

Allocation debris belonging only to:
- Draft;
- Cancelled;
- Completed/inactive historical WOs

must not become physical occupancy.

Such rows should be:
- reported by diagnostics/cleanup if required;
- not treated as active capacity solely because a row exists.

However, an allocation claiming to belong to an active WO but not matching its current selected machine/revision is an active integrity blocker.

This aligns the runtime rule with current Planner readiness semantics.

---

# 12. Phase 6 — canonical deterministic Work Order placement engine

This is the largest correction missing from v3.

Do not bolt occupancy subtraction independently into Draft and Release.

Extract/refactor one canonical Work Order placement engine that preserves the current route semantics:

```text
StageSequence order
-> route-step deterministic order
-> ProcessSequence order
-> operation deterministic order
-> WIP predecessor constraints
-> Forward/Backward direction
```

It must accept:

```text
external occupied intervals by physical machine
```

and maintain:

```text
local proposed occupied intervals by physical machine
```

throughout the entire Work Order calculation.

---

## 12.1 Same-Draft self-capacity

After placing one selected machine operation, immediately append its consumed intervals to local occupancy.

Any later operation in the same calculation using the same physical machine must see those intervals.

This prevents the Draft from overlapping itself.

Equal process/stage sequence means "no precedence dependency"; it does **not** create a second physical machine.

If two parallel-eligible operations use one physical machine, capacity may serialize them.

The deterministic tiebreaker must be stable:
- existing operation ordering;
- then UID/source key as appropriate.

Add tests so this is not accidental.

---

## 12.2 Preserve raw-calendar source hash

`ScheduleSourceHash` continues to hash the raw machine calendar/provenance.

Do not include dynamic occupancy in the calendar source hash.

Capacity staleness is handled by Release revalidation.

---

## 12.3 Horizon expansion

Each scheduler horizon attempt must have occupancy for the same horizon.

Do not load only the first horizon's allocations and then extend the calendar without extending capacity evidence.

Capacity query:

```text
StartDateTime < horizonEnd
AND EndDateTime > horizonStart
```

using the existing machine/start/end index.

---

# 13. Phase 7 — Draft / Recalculate behavior

Draft/Recalculate:

1. obtains current active occupancy for affected machines;
2. runs the canonical placement engine;
3. includes same-Draft local occupancy;
4. updates the Draft's planned dates/provenance;
5. recomputes normal Draft SnapshotHash/Revision as existing logic already does;
6. does **not** create `PrMachineScheduleAllocation` rows.

Do not acquire exclusive physical machine locks merely to calculate a Draft proposal.

A race is allowed to make the Draft stale.

Release is the authority.

---

# 14. Phase 8 — Release revalidation must use the same placement engine

Current Release should not continue independently iterating flattened selected-machine rows.

Under locks:

1. obtain external verified occupancy;
2. run the **same deterministic Work Order placement engine** against the Draft's frozen production inputs;
3. compare calculated placement with saved Draft placement/provenance.

If any saved operation/machine outer Start/Completion must move:

```text
MACHINE_CAPACITY_STALE
```

Keep the WO Draft.

User recalculates and reviews the new dates.

If placement remains valid:
- use the calculated consumed intervals from that same plan;
- write allocation fragments;
- Release atomically.

This eliminates Draft/Release ordering drift.

---

# 15. Release lock set and transaction order

## 15.1 Lock machine codes

Before capacity mutation, seed:

```text
current selected machine codes
UNION
existing allocation machine codes owned by this WO
```

Normalize/distinct/sort.

Acquire:

```text
ProductionMachineScheduleLock.AcquireManyAsync
```

using the same Release DbContext/transaction.

Then:

```text
WorkOrderSchedulingLock(shared)
ProductionWorkOrderExecutionLock
```

Re-read the relevant state after locks.

If a newly discovered owned allocation references an unlocked machine:
- abort with concurrency;
- retry from a fresh command.

Do not delete an old-machine allocation without holding that old physical-machine lock.

---

## 15.2 Allocation replacement boundary

On successful Release/re-release:

remove all current-state allocation rows for:

```text
CompanyCode
BranchCode
WorkOrderId
```

then write the new selected-machine fragments returned by the canonical placement plan.

Do not delete allocations of any other Work Order.

---

# 16. Phase 9 — Planner Reschedule must stop raw-calendar runtime reconstruction

Current `MachineScheduleRescheduleService.LoadExistingCapacityAsync`:
- filters positive-duration rows;
- reconstructs each active job against raw calendar;
- compares saved allocations to reconstructed raw-calendar intervals.

That is exactly the mismatch that breaks a valid finite-capacity rescheduled job.

Refactor runtime capacity loading to the shared allocation-authoritative service.

Keep:
- same-machine move only;
- Released/InProgress only;
- forward-only Planner move restriction;
- execution blockers;
- Daily Production blockers;
- preview token;
- rowversions;
- ScheduleRevision;
- audit;
- atomic allocation replacement.

Do not weaken Planner safety to fix Release.

---

# 17. Phase 10 — Reopen allocation cleanup and lock order

A successful:

```text
RELEASED -> DRAFT
```

must free current machine allocation rows in the same transaction.

But lock order must respect the existing repository.

## 17.1 Existing observed lock contracts

- Release/Planner/Backfill:
  `machine applock -> scheduling lock -> execution lock`
- Daily Production Post:
  `branch stock lock -> execution lock -> Work Order row`
- Material Issue Draft:
  locks Work Order row but does not wait for machine/execution applock in that path.
- Reopen currently:
  Demand/DR bridge -> Work Order lifecycle row.

## 17.2 Revised Reopen sequence

For DR-backed WOs:
1. existing Demand/DR bridge locking stays first;
2. read-only seed of selected + owned allocation machine codes;
3. machine locks in sorted order;
4. scheduling shared lock;
5. Work Order execution lock;
6. Work Order lifecycle row lock;
7. recheck RowVersion/status/blockers;
8. re-read allocation machine codes;
9. if unlocked code appears -> concurrency failure;
10. delete all current WO allocation rows;
11. transition to Draft;
12. reconcile DR;
13. audit;
14. commit.

For non-DR WOs omit only the DR bridge.

Mandatory SQL tests must prove this does not deadlock with:
- Material Issue draft/post;
- Daily Production post;
- Planner reschedule;
- Release;
- FG/production stock transactions that lock the WO.

Do not claim the lock order safe until those tests pass.

---

# 18. Snapshot integrity after Planner reschedule — explicitly unresolved in the critical fix

This is a separate architectural issue.

Facts from current repository:
- V4 hash contains schedule fields and production/cost facts.
- Planner schedule-only commit changes dates/provenance but intentionally freezes SnapshotHash.
- therefore full `MatchesStoredSnapshotHash()` cannot verify a rescheduled Released aggregate.
- Planner reschedule itself currently does not call `MatchesStoredSnapshotHash()`.

## Do not solve this by blind V4 recomputation

Blind recomputation can bless unrelated production-data tampering.

## Do not silently switch to V5 inside the capacity patch

That creates migration risk for V4 Drafts and downstream posting tokens.

## Safe policy for this plan

The capacity/release fix may proceed without changing the hash contract.

But **full Machine Planner production certification remains blocked** until a separate integrity design is chosen and tested.

Recommended follow-up design:
- introduce a schedule-independent production-content integrity fingerprint/hash separate from the existing execution SnapshotHash;
- preserve existing SnapshotHash tokens for posting compatibility;
- Planner reschedule verifies the content fingerprint before and after schedule-only mutation;
- Reopen can then safely re-baseline the schedule-bearing Draft hash only after content integrity is proven.

Existing already-rescheduled rows need a controlled certification/backfill policy; they cannot automatically prove past integrity.

This is intentionally not hand-waved as "done".

---

# 19. Scheduling precision issue — separate but documented

Current scheduler uses rounded `PlannedRunMinutes`.

This produces small millisecond drift and can round very small CycleSeconds to zero.

For the critical capacity fix:
- preserve current duration formula;
- Product Definition activation must reject a timing basis that cannot produce a positive one-slot duration under current precision;
- do not change historical schedule math.

Separate follow-up option:
derive runtime duration from frozen:

```text
PlannedCycleSlots * CycleSeconds
```

in seconds rather than rounded minutes.

That requires:
- migration/compatibility analysis;
- timestamp regression;
- SnapshotHash impact analysis;
- backfill/reconstruction tests.

Do not mix this precision redesign into the urgent blocker fix.

---

# 20. Data remediation for the diagnosed records

## FG002 Product Definition

Correct the actual engineering/business timing through the existing Product Definition service/UI.

Do not invent a value.

Do not copy another product's cycle time without business evidence.

---

## WO00000004 — Draft

After Product Definition correction:

```text
More -> Update from Product Definition
```

is required.

Simple `Recalculate Schedule` must not silently fetch new mutable master timing into the frozen snapshot.

After refresh:
- recompute quantities/cycles;
- confirm positive duration;
- finite-capacity recalculate;
- Release;
- verify allocations.

---

## WO00000002 — In Progress

Do not manually SQL-edit:
- CycleSeconds;
- PlannedRunMinutes;
- dates;
- IsSelected;
- SnapshotHash;
- allocations.

After `LEGACY_EMPTY_CAPACITY` compatibility logic it should stop blocking future valid WOs.

Its historical schedule defect remains a diagnostic/history issue.

Any repair requires a separate audited service-layer repair process.

---

# 21. Planner readiness vs Release correctness

Do not make Work Order Release depend on:

```text
MachineSchedulePlannerWriteEnabled
```

The Planner UI feature flag is not a stock/production Release permission.

Release capacity integrity must operate independently.

Planner write readiness may remain company-wide.

Runtime Release/WO scheduling should only fail for integrity problems relevant to the affected physical machine(s).

---

# 22. Mandatory tests — zero-duration and corruption

### Legacy empty
- active zero-duration;
- Start == Completion;
- no allocations;
- valid new WO on same machine releases.

### Corrupt zero + positive span
- duration = 0;
- Start < Completion;
- Release must block with exact integrity error.

### Corrupt zero + allocation
- duration = 0;
- positive current allocation fragment;
- block.

### Negative data
- negative timing component;
- never classify as legacy zero;
- block.

### New Draft zero
- selected duration = 0;
- scheduler/readiness block before Release.

---

# 23. Mandatory tests — Product Definition duration

- MACHINE all zero -> activation blocked.
- MACHINE Cycle=0, Setup positive -> allowed.
- MACHINE Cycle positive and representable under current precision -> allowed.
- tiny positive Cycle that rounds one-slot PlannedRunMinutes to zero, with no fixed seconds -> activation blocked.
- AUTOMATED same rule.
- PACKING with machines same rule.
- PACKING without machines retains StandardDuration rule.
- fixed-duration process types unchanged.

---

# 24. Mandatory tests — same-Work-Order capacity

- two serial operations same M1 -> no overlap.
- two equal ProcessSequence operations same M1 -> physical capacity prevents overlap.
- two equal ProcessSequence operations different machines -> may run concurrently.
- two same StageSequence route steps sharing M1 -> no physical overlap.
- forward direction.
- backward direction.
- deterministic result independent of EF collection enumeration order.

This is a mandatory new group; v3 did not include it strongly enough.

---

# 25. Mandatory tests — external finite capacity

- active allocation at shift start.
- active allocation in middle of shift.
- split consumed intervals.
- breaks.
- preventive maintenance.
- holiday/off day.
- overnight.
- half-open adjacency.
- horizon extension reloads occupancy.
- cross-branch same machine.
- different products same machine.
- same product different machines.

---

# 26. Mandatory tests — Release equivalence

For the same saved Draft and same capacity evidence:

```text
Draft canonical plan
==
Release canonical revalidation plan
```

for:
- Start/Completion;
- selected machine;
- calendar source hash;
- consumed productive interval total.

If capacity changed:
- no partial allocations;
- status stays Draft;
- message asks user to Recalculate.

---

# 27. Mandatory tests — active allocation integrity

For positive active capacity:

- missing current allocation -> block;
- stale ScheduleRevision -> block;
- wrong machine -> block;
- wrong operation -> block;
- wrong active WO -> block;
- source hash mismatch -> block;
- active orphan -> block;
- overlap -> block.

Inactive historical allocation debris:
- must not consume runtime capacity merely because the row exists.

---

# 28. Mandatory SQL Server concurrency matrix

Use the existing scratch SQL Server fixture.

Do not accept SQLite-only evidence.

Add:

1. different WO-A Release vs WO-B Release on same M1;
2. Release vs Planner reschedule same M1;
3. Planner reschedule vs Planner reschedule same M1;
4. Reopen WO-A vs Release WO-B into freed M1 capacity;
5. Reopen vs Planner reschedule;
6. Reopen vs Daily Production post;
7. Reopen vs Material Issue draft;
8. Reopen vs Material Issue post;
9. Reopen vs FG/production transaction that locks same WO;
10. Draft stale capacity then concurrent Release;
11. old-machine cleanup M1 -> new selected M2 while another command uses M1;
12. cross-branch Release race on same physical machine.

Post-condition:

```text
No two committed active positive-duration allocations overlap on CompanyCode + MachineCode.
```

Also prove:
- no deadlock;
- no partial status/allocation mutation;
- concurrency failure leaves previous state intact.

---

# 29. Performance gates

Capacity queries must be bounded by:
- CompanyCode;
- affected MachineCode(s);
- horizon.

Use overlapping-window predicates and existing index:

```text
IX_PrMachineScheduleAllocation_Machine_Start_End
```

Requirements:
- batched machine queries;
- no N+1 active-WO query per fragment;
- no reconstruct-every-active-WO loop;
- no full historical allocation scan;
- read-only probes `AsNoTracking`;
- same transaction context for protected runtime reads.

Before production enablement:
- capture SQL Server query plan;
- test representative large active schedule volume.

Do not add an index only because the plan says so; add one only if the actual query plan needs it.

---

# 30. Costing / quantity / WIP invariants

The capacity fix must not change:

- PlannedQty business quantity;
- Good/Scrap/Reject/Hold/Rework;
- ProcessedQty;
- material Required/Issued/Returned qty;
- WIP/Production balance;
- FG quantity;
- CostLayer;
- FIFO;
- WAC;
- Standard Cost;
- stock ledger;
- GL;
- posting links;
- posting/rollback rules;
- month-end;
- BusinessDate.

Do not retroactively recost `WO00000002`.

Do not use a schedule repair as a costing repair.

---

# 31. Recommended implementation split

Do not implement this as one giant commit.

## Change A — zero-duration blocker and prevention
- regression tests;
- duration helper;
- safe legacy-empty classification;
- Product Definition activation rule;
- WO readiness;
- scheduler zero-duration guard;
- diagnosed Draft refresh flow.

## Change B — canonical finite-capacity Work Order planner
- shared capacity evidence service;
- caller-owned DbContext API;
- external capacity;
- same-Draft local capacity;
- deterministic placement;
- forward/backward;
- horizon-aware occupancy.

## Change C — Release/Planner authority
- Release uses same placement engine;
- allocation-authoritative active capacity;
- capacity-stale flow;
- Planner removes raw-calendar runtime reconstruction;
- affected-machine integrity handling.

## Change D — lifecycle cleanup/concurrency
- whole-WO allocation replacement;
- selected + old allocation machine locks;
- Reopen allocation cleanup;
- SQL Server race matrix.

## Separate Change E — snapshot integrity hardening
- do not merge until design is explicitly reviewed;
- no blind rehash;
- no unreviewed V5 migration.

---

# 32. What is actually safe to say now

Based on current repository code plus the supplied database diagnosis:

**Confirmed**
- zero-duration `WO00000002` can block later Release;
- `WO00000004` also has zero duration and needs source-definition correction + Draft refresh;
- Release is inconsistent with other zero-duration capacity consumers;
- Draft scheduling is not active-allocation-aware;
- Planner reschedule is finite-capacity-aware;
- Planner runtime capacity currently reconstructs active schedules from raw calendar;
- Release does the same;
- same-Draft self-capacity is not currently modeled by `WorkOrderScheduleCalculator`;
- allocation writes have a positive-interval DB constraint;
- current machine locks are transaction-owned;
- SnapshotHash V4 includes schedule values;
- Planner schedule-only mutation freezes SnapshotHash;
- scheduling uses rounded PlannedRunMinutes.

**Not yet proven**
- final deadlock-free Reopen lock order;
- all cross-branch races;
- performance at production volume;
- snapshot-integrity strategy after repeated schedule-only moves;
- behavior after implementation because no implementation exists yet.

Those unproven items must stay unproven until tests provide evidence.

---

# 33. Current verdict

## Previous v3
**NOT ACCEPTED as final.**

## This v4 plan
**Reviewed corrective roadmap — no numeric score.**

It is suitable to guide implementation in phases, but I am **not** claiming that the machine planner is production-safe before implementation and test evidence.

## Production enablement
**NOT APPROVED.**

Production enablement requires evidence from:
- exact regression tests;
- same-Draft physical-capacity tests;
- active-allocation integrity tests;
- SQL Server race/deadlock suite;
- large-volume query/performance validation;
- explicit decision on post-reschedule snapshot integrity.

No "10/10" statement should be made before those results exist.
