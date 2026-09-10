---
name: Approval Excel Import
overview: Add a single Approval Workflow Excel import/export for ~800 rules. One workbook covers workflow definitions and assignments. Preview validates against current tenant data, then persist uses atomic per-workflow transactions and per-module assignment merge (never wipe). Core approval services stay Excel-agnostic.
todos:
  - id: workflow-code
    content: Add WorkflowCode column, unique index after backfill/normalize, expose on Maintenance; identity match with ID/Code conflict ERROR and name-fallback WARNING
    status: pending
  - id: atomic-save
    content: SaveWorkflowGraph is the only persist path (popup SaveWorkflow calls it); one transaction for code gen, ClearDefault, header, steps, conditions
    status: pending
  - id: merge-service
    content: MergeAndSaveAssignments load+merge+validate+save in one DbContext transaction; Assignment.Module must equal Workflow.Module; Priority rules
    status: pending
  - id: dtos-service
    content: Hierarchical DTOs, shared ValidateWorkflow/ValidateAssignments, ImportValidationResult with persistence status, tenant-scoped lookups, server-side auth
    status: pending
  - id: excel-template
    content: ClosedXML v1.0 workbook; WorkflowCode joins; Reference documents all rules; dropdowns UX-only; invariant numeric format
    status: pending
  - id: import-page
    content: Import page with summary, OK/WARNING/ERROR, persistence FAILED distinct, partial-import message, menu 1000.08 + localization
    status: pending
  - id: existing-screens
    content: Wire Export on Workflow Maintenance; add Import/Export on Maintenance and Assignment
    status: pending
  - id: tests
    content: Tests including ID/Code conflict, module mismatch, duplicate code/approver, priority, default+clear atomic, merge one-tx, name WARNING, persist vs validate fail
    status: pending
isProject: false
---

# Approval Workflow Excel Import

## Why this shape

The 800-row pain is almost always **assignments**, but customers also need the **workflows** those assignments point at. One workbook + one import page covers both.

Existing [`SaveAssignments`](WincomHRM_Classes/HelperClass/Approval/ApprovalWorkflowAssignmentService.cs) **replaces every assignment for a module**. Excel import must never pass Excel rows straight into that method.

Existing [`SaveWorkflow`](WincomHRM_Classes/HelperClass/Approval/ApprovalWorkflowMaintenanceService.cs) is **not atomic today**: [`AddWorkflow`](WincomHRM_Classes/Repository/Approval/ApprovalWorkflowRepository.cs) / `UpdateWorkflow` / `ReplaceWorkflowSteps` / `ReplaceWorkflowConditions` each open a **new DbContext**. Import cannot rely on that as-is.

**Layering:** core approval services do **not** know about Excel. Excel/UI parses to import DTOs; domain exposes `ValidateWorkflow`, `SaveWorkflowGraph`, `ValidateAssignments`, `MergeAndSaveAssignments`. No `ImportExcel` method on the approval domain.

```mermaid
flowchart TD
  upload[Upload file]
  fileCheck[Size format version sheets columns]
  parse[Parse ClosedXML]
  graph[Build hierarchical DTO]
  refs[Load tenant reference data]
  auth[Authorize import export company branch module]
  validate[Validate identity header steps conditions assignments and combined DB state]
  preview[Preview with summary]
  importClick[User clicks Import]
  reAuth[Re-check authorization]
  persistWf[Each valid workflow SaveWorkflowGraph one transaction]
  persistAsg[Each module load merge validate save one transaction]
  result[Show persistence result partial import message]
  upload --> fileCheck --> parse --> graph --> refs --> auth --> validate --> preview --> importClick --> reAuth --> persistWf --> persistAsg --> result
```

## Import behaviour (must be obvious in UI and Reference sheet)

- **Workflow header:** upsert
- **Workflow steps:** full replace of that workflow's steps (rows not in Excel are deleted)
- **Workflow conditions:** full replace of that workflow's conditions
- **Assignments:** merge
- **Existing assignments not in Excel:** keep
- **Bulk delete:** not supported
- **Warnings do not prevent import. Errors are not imported.** Successful workflows/modules stay committed if a later item fails (not one giant transaction).

## Workflow identity

Add persisted `WorkflowCode` NVARCHAR(50) on [`ApprovalWorkflowDefinition`](WincomHRM_Model/Model/Approval/ApprovalWorkflowDefinition.cs).

**Normalization (app always, even if SQL collation is CI):** trim, then case-insensitive compare / store as trimmed upper (or trimmed original + compare ordinal ignore case — pick one and use it everywhere). Unique index and lookups must use the **same** scope: `CompanyCode + BranchCode + Module + WorkflowCode`.

**Migration (do not create the unique index first):**

1. Add nullable column
2. Backfill `{MODULE}-{Id}` (example `LEAVE-15`)
3. Trim/normalize
4. Detect remaining duplicates; fail migration with a clear list if any
5. Make NOT NULL
6. Create unique index `UX_ApvWorkflowDefinition_CompBranchModuleCode`

SQL: [`SQLTable/Table/ApvWorkflowDefinition.txt`](SQLTable/Table/ApvWorkflowDefinition.txt) + dated migration. Test against production-like existing data.

**Code generation (same transaction as insert):**

```
BEGIN TRANSACTION
  insert header (user-supplied code if present, else temporary/empty)
  obtain Id
  if code blank: set `{MODULE}-{Id}` and update header
  if IsDefault: ClearDefaultForScope then set this row default
  replace steps + approvers
  replace conditions
COMMIT
```

Never generate `{MODULE}-{Id}` after the transaction commits. Collision with an existing code in the same company/branch/module = **ERROR** (`WorkflowCode 'LEAVE-MGR' already belongs to another workflow.`), never a raw unique-index exception.

Excel **Workflows** sheet: WorkflowID (blank if new), Module, WorkflowCode (required join key), WorkflowName, IsDefault, IsActive, LastUpdatedOn (export only).

**Matching (tenant-scoped; never `FindAsync(id)` without company/branch):**

1. If WorkflowID **and** WorkflowCode both supplied: both must resolve to the **same** workflow. Mismatch = ERROR (`WorkflowID 15 and WorkflowCode LEAVE-MGR identify different workflows.`).
2. Else if WorkflowID supplied: match ID in current company/branch.
3. Else if WorkflowCode supplied: match Code + Module in current company/branch.
4. Else Module + WorkflowName (case-insensitive): **WARNING** (`Matched using Module + WorkflowName because WorkflowID and WorkflowCode were not supplied.`).

Steps, Conditions, Assignments join **only** on WorkflowCode. Unknown Code = ERROR; skip the whole workflow if it is a step/condition.

## Excel template (generated ClosedXML)

Generate in UI (ClosedXML already in [`WincomHRMUI.csproj`](WincomHRM_UI/WincomHRMUI.csproj)). Do not ship a static xlsx. Do **not** trust the Meta/Reference sheet for business rules; server remains authoritative.

Sheets:

- **Meta / Reference** — `TemplateVersion = 1.0`, generated date, and a built-in manual: identity rules, code/ID conflict, name-fallback warning, step/condition replace vs assignment merge, blank scope OR matching, condition AND + invariant numerics, approver sources, priority, delete not supported, warning vs error, partial-import meaning
- **Workflows / Steps / Conditions / Assignments** as specified above

Instruction block + **Start From Here**. Dropdowns are **UX only**.

**File safety:** 4MB; reject unsupported TemplateVersion; required sheets/columns; extra columns ignored; empty / missing Start From Here = ERROR; caps 2,000 workflows / 5,000 steps / 2,000 conditions / 5,000 assignments.

## Condition semantics (must match runtime)

[`WorkflowResolver.EvaluateConditions`](WincomHRM_Classes/HelperClass/Approval/WorkflowResolver.cs): multiple active conditions = **AND**. No OR/groups in v1.

Field + type + operators (same fields as [`GetConditionFieldOptions`](WincomHRM_UI/UI/Approval/WorkflowMaintenance.razor.cs)):

- Numeric (`LeaveDay`, `Amount`, `OTHours`, `OTRate`): `= != > >= < <=`. **Invariant decimal** (`1000.50`, no thousands separator, `.` as decimal). Reject `CONTAINS`, empty, and values that fail `decimal.TryParse(..., InvariantCulture)`.
- String/id (`LeaveTypeID`, `EmployeeID`, `ClaimType`, `ClaimName`): `= != CONTAINS`. **Also** resolve the ID against current-tenant master data (leave type for Leave module, employee, claim type as applicable). Unknown ID = ERROR, not datatype-only.
- Field must belong to the workflow's Module.

## Transaction and atomicity

**`SaveWorkflowGraph` is the only workflow persist path.** Popup [`SaveWorkflow`](WincomHRM_Classes/HelperClass/Approval/ApprovalWorkflowMaintenanceService.cs) must call it. Shared `ValidateWorkflow` is used by popup and import so rules cannot diverge.

One DbContext + `BeginTransactionAsync` per workflow:

```
BEGIN TRANSACTION
  upsert header (and generate WorkflowCode if blank)
  if becoming default: ClearDefaultForScope then set this default
  replace steps + approvers
  replace conditions
COMMIT
```

If save of B-as-default fails, A must still be default (`ROLLBACK` includes the clear). Never clear default in a separate context/transaction.

**Assignments — one DbContext/transaction per module (load + merge + validate + save):**

```
BEGIN TRANSACTION
  load current assignments (same context)
  merge Excel rows in memory
  ValidateAssignments on merged list
  save merged list
COMMIT
```

Do **not** read on Context A and write on Context B (another user could change rows in between). Unexpected DB errors roll back **that** transaction only.

Import as a whole is **not** one giant transaction. Partial success is allowed and must be explained in the result UI.

## Assignment merge semantics

`MergeAndSaveAssignments(module, incoming, user)` on [`IApprovalWorkflowAssignmentService`](WincomHRM_Model/Inferface/Approval/IApprovalWorkflowAssignmentService.cs). Excel-agnostic. Never call `SaveAssignments` with Excel-only rows.

Natural key: `Module + EmployeeID + DepartmentID + DesignationID` (blank = all), trim + upper.

- Scope missing → **INSERT**
- Scope exists → **UPDATE** WorkflowId, Priority, IsActive (keep Id / CreatedBy / CreatedOn; set UpdatedBy/On)
- Scope in DB not in Excel → **KEEP**

**Assignment.Module must equal the resolved workflow's Module.** Claim assignment to a Leave workflow = ERROR even if the Code exists.

**Priority:**

- Required integer `>= 1`
- Missing, non-integer, or `< 1` = ERROR
- After merge, existing save already re-numbers 1..n by `OrderBy(Priority)` (see [`NormalizeAssignments`](WincomHRM_Classes/HelperClass/Approval/ApprovalWorkflowAssignmentService.cs)). Duplicate Priority in the same module = **WARNING** (order among ties is not guaranteed until re-number)
- Resolver uses this stored order; do not invent a second sort

**Overlap:** exact duplicate scope = ERROR. Potential OR-overlap = WARNING. Resolver OR behaviour unchanged.

**ApproverEmployeeIDs:** split on `;`, trim each token (`E001; E002` valid). Empty tokens ignored. Duplicate IDs after normalize = **ERROR** (data-entry mistake). Each ID must be a current-tenant IsApprover employee.

Lookups always `user.compCode` / `user.branchCode`. Cross-company IDs are invalid.

## Default workflow integrity

Combined DB + Excel: at most one active default per module.

- Two `IsDefault=Y` in file for same module = ERROR
- Excel B default while DB A is default = allowed; WARNING that A will be cleared; clear+save B **inside** `SaveWorkflowGraph`
- `IsDefault=Y` requires `IsActive=Y`
- Active workflow must have **at least one active step** (popup and import share this)

## Failed Excel workflow vs existing DB workflow

If Excel definition fails but a matching tenant workflow exists, assignments may still import with **WARNING**: Excel definition not applied; existing DB workflow will be used.

## Authorization (server-side, not menu-only)

On Preview and again on Import, `CanAccess` for:

- Import page (`WORKFLOW_TABLE_IMPORT`)
- Export (same or workflow maintenance)
- Workflow maintenance / assignment as needed
- Current company/branch
- Requested modules

Unauthenticated or denied = no persist.

## Validation vs persistence status

User-facing grid: **OK / WARNING / ERROR**.

Internal/final result also tracks **VALIDATED / IMPORTED / SKIPPED / FAILED**. Example: validation OK but DB timeout → Status ERROR, persistence FAILED, message `Database timeout`. Do not treat that as a validation error from Preview.

After import, show a **partial-import summary**:

- Imported: N workflows, M assignments
- Failed: X workflows, Y assignments
- Explicit: successfully committed items were **not** rolled back

## Architecture

```
ClosedXML (UI) -> flat DTOs -> BuildGraph
  -> shared ValidateWorkflow / ValidateAssignments
  -> ImportValidationResult (preview)
  -> re-auth + revalidate
  -> SaveWorkflowGraph / MergeAndSaveAssignments
```

`ImportValidationResult`: per-sheet totals; per-row Status + Message + PersistenceStatus; `CanImport` if any row is OK (warnings allowed).

## UI

[`WincomHRM_UI/UI/Approval/ImportApprovalWorkflow.razor`](WincomHRM_UI/UI/Approval/ImportApprovalWorkflow.razor) `/approval/workflow-import`.

Download Template, Export Current, Preview, Import (`Import 792 valid rows`). Near the button: warnings do not block; errors are skipped. Four tabs: **RowNo | Status | Message**. Concurrency WARNING text includes workflow identity, Excel `LastUpdatedOn`, and current DB `UpdatedOn`.

Wire unused Export on [`WorkflowMaintenance.razor.cs`](WincomHRM_UI/UI/Approval/WorkflowMaintenance.razor.cs); add Import/Export on Maintenance and Assignment.

Menu `WORKFLOW_TABLE_IMPORT = "1000.08"`. Strings in [`ApprovalResource.resx`](WincomHRM.Resources/Localization/ApprovalResource.resx) (+ ms-MY, zh-CN, zh-TW).

## Concurrency (v1)

`LastUpdatedOn` WARNING only (with both timestamps). No RowVersion.

## Tests

Extend [`WincomHRMCore.Tests/Approval`](WincomHRMCore.Tests/Approval) with previous cases **plus**:

- WorkflowID and WorkflowCode point to different records → ERROR
- Assignment.Module differs from Workflow.Module → ERROR
- Duplicate WorkflowCode → clean ERROR (not SQL exception)
- Duplicate ApproverEmployeeID → ERROR; `E001; E002` trims OK
- Priority missing / `< 1` → ERROR; duplicate priority → WARNING
- ClearDefault + new default rolls back together if steps fail
- Assignment merge read+write uses one transaction (no orphan/partial module write)
- Name-only fallback → WARNING
- Persistence failure distinct from validation failure
- Tenant-scoped WorkflowID (other-company ID rejected)
- LeaveTypeID / employee condition values unknown in tenant → ERROR
- Export → import round-trip; ~800 assignment merge

## Out of scope

- Changing resolver OR-scope behaviour
- Bulk delete via Excel
- Condition OR / groups
- Importing in-flight approval requests
- Hard optimistic concurrency / RowVersion
