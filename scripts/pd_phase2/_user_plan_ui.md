Product Definition UI

Add Definition Code / Name / Default.

Remove effective date inputs.

Update canonical routes.

Update list columns/actions.

Add child Product Definition picker for separate-definition BOM components.

Update help.

Phase 4 — BOM tree/explosion

Definition-aware structure cache.

SupplySource-driven recursion.

Definition-aware circular validation.

Definition-aware BOM Explorer.

Remove As-of Date UI.

Phase 5 — Work Order snapshot engine

Replace effective date with DefinitionCode.

Update loader.

Update snapshot builder.

Copy SourceDefinitionCode/Name.

Copy material ComponentDefinitionCode.

Update hashes/hash versions.

Move current snapshot format to v3.

Make current snapshot builder the single Work Order build engine.

Phase 6 — Work Order UI

Remove BOM as-of date.

Add Production Definition dropdown.

Auto-select default.

Update fingerprints.

Show selected definition/revision.

Update list.

Update How To Use.

Add controlled Change Definition for Draft.

Phase 7 — refresh / audit / cleanup

Refresh same DefinitionCode to its current ACTIVE revision.

Add Definition Changed audit flow.

Verify old snapshots remain readable.

Drop obsolete date columns.

Remove obsolete date code/helper methods.

Remove/deprecate the old Work Order prepared-snapshot path.

37. Manual Verification — No Unit Tests

No new unit tests are required for this implementation.

Run project compile/build checks and manually verify these scenarios.

Scenario 1 — first/default definition

Create:

FG001 / STANDARD / V1

Activate.

Expected:

ACTIVE
Default = Yes

New Work Order for FG001 automatically selects STANDARD.

Scenario 2 — second definition same product

Create:

FG001 / LINE-B / V1

Activate.

Expected:

STANDARD V1 ACTIVE
LINE-B   V1 ACTIVE

Neither activation supersedes the other.

Scenario 3 — revision inside one definition

Create:

FG001 / STANDARD / V2

Activate.

Expected:

STANDARD V1 SUPERSEDED
STANDARD V2 ACTIVE
LINE-B V1 ACTIVE

Scenario 4 — Work Order selection

Create WO:

FG001
Definition = LINE-B

Expected snapshot:

SourceDefinitionCode = LINE-B
SourceBomVersion = 1
SourceBomHdrID = exact LINE-B revision UID

Scenario 5 — historical snapshot

After WO created from LINE-B V1:

Activate LINE-B V2.

Expected existing WO:

still LINE-B V1 snapshot

No silent change.

Scenario 6 — refresh

Refresh the Draft.

Expected:

compare LINE-B V1 → LINE-B V2
confirm
snapshot rebuilt
SnapshotRevision++

It must not switch to STANDARD.

Scenario 7 — change definition

Draft WO currently:

FG001 / STANDARD

Change to:

FG001 / LINE-B

Expected:

diff shown;

snapshot rebuilt;

overrides reset;

schedule recalculated;

SnapshotRevision++;

audit records definition change.

Scenario 8 — released Work Order

Release WO.

Expected:

Product Definition read-only
Change Definition unavailable
Refresh unavailable

Scenario 9 — child separate Product Definition

Parent:

FG001 / STANDARD

BOM:

WIP001
SupplySource = SEPARATE_PRODUCT_DEFINITION
ComponentDefinitionCode = LINE-B

Expected BOM Explorer follows:

WIP001 / LINE-B

not WIP001's default STANDARD unless that is what was explicitly selected.

Scenario 10 — internal WIP

BOM line:

WIP002
SupplySource = INTERNAL_ROUTE_WIP

Expected:

no child Product Definition lookup;

current route step remains producer.

Scenario 11 — circular definition graph

Example:

A[STANDARD] → B[LINE-B]
B[LINE-B]   → A[STANDARD]

Expected activation/save blocked with clear cycle path.

Scenario 12 — delete exact definition

WO exists for:

FG001 / STANDARD

No references exist to:

FG001 / LINE-B

Expected:

deleting LINE-B is not blocked merely because another FG001 definition has a WO.

If a parent BOM or WO references LINE-B, deletion is blocked.

Scenario 13 — no date dependency

Verify no Product Definition/Work Order user flow asks for:

BOM as-of date
Effective From
Effective To

Scheduling still uses:

Planned Start
Planned Completion
Schedule Direction

and works independently.

38. Build Verification

No unit-test work is required.

At minimum build the production projects after each major phase:

ErpWeb.Model
ErpWeb.Core
ErpWeb.UI
ErpWeb

Do not spend implementation time creating new test classes.

If the existing test project fails to compile only because public DTO signatures changed, either perform minimal compile-maintenance there or build the production projects separately, according to the repository's normal CI/build setup.

39. Acceptance Criteria

Implementation is ready when all are true:

Same product can have multiple Product Definitions.

Each Product Definition has its own revision sequence.

Multiple different definitions for one product can be ACTIVE together.

Only one ACTIVE revision exists per Product + Definition.

One ACTIVE definition can be marked default per product.

Work Order user selects Production Definition.

BOM as-of date is removed from Work Order.

Effective From / To no longer drive Product Definition selection.

New WO automatically selects default definition where available.

Exact source revision is frozen into the Work Order.

Existing WO is not silently changed after Product Definition revision changes.

Refresh stays within the same DefinitionCode.

Draft can deliberately change to another definition through a controlled rebuild.

Released Work Order cannot switch definitions.

Multi-level separate Product Definition BOM lines identify the child DefinitionCode.

BOM structure/explosion follows child DefinitionCode deterministically.

Circular BOM validation is definition-aware.

Product Definition list supports multiple definitions per product.

Product Definition routes include DefinitionCode.

BOM Explorer is definition-aware and has no As-of Date.

Work Order list/detail show source DefinitionCode/Name/Revision.

Snapshot/source hashes no longer depend on obsolete effective dates.

Snapshot format/hash versions are updated safely.

Delete checks operate on the exact definition rather than all definitions of the same product.

Existing historical Work Order snapshots remain readable.

Production projects compile successfully.

Manual scenarios above pass.

40. Final Recommended Model

IvStockMaster
   Product FG001
        |
        +------------------------------------------------+
        |                                                |
        v                                                v
PrBomHdr                                          PrBomHdr
FG001 / STANDARD                                  FG001 / LINE-B
V1 SUPERSEDED                                     V1 ACTIVE
V2 ACTIVE
        |                                                |
        +--------------------+---------------------------+
                             |
                             v
                     Work Order Entry
                     Product = FG001
                     Definition = LINE-B
                             |
                             v
                  Resolve ACTIVE LINE-B revision
                             |
                             v
                   Freeze into WO snapshot
                             |
                    +--------+--------+
                    |                 |
                    v                 v
             WO source identity   WO copied detail
             FG001 / LINE-B / V1  Route / Process
             exact PrBomHdr UID   BOM / Machine
                                  Labour / Schedule

The key architectural rule is:

Product Definition selection is explicit; revision selection is automatic; Work Order history is frozen.

This removes the confusing date dependency while preserving the strongest part of the current implementation: exact revision provenance and an independent Work Order snapshot.
</user_query>