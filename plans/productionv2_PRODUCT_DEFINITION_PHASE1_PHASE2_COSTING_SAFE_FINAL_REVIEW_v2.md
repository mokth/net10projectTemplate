# productionv2 — Product Definition Enhancement
## PHASE 1 + PHASE 2 ONLY — Strict AI Coding Agent Implementation Plan

**Prepared:** 31 October 2026 (Malaysia)  
**Repository:** `mokth/net10projectTemplate` — branch `productionv2`  
**Review reference:** GitHub `productionv2` commit `2b348746c40055aa322bd07d3f15d46b0bd35e32`.  
**Agent source of truth:** the **CURRENT LOCAL** `C:\wincom\net10projects` working tree at implementation time. Reinspect it first: local changes may be newer than the reference commit.  
**Review status:** **REVISED — CONDITIONAL IMPLEMENTATION PLAN (not yet 10/10 deployment-approved).** Phase 1 and Phase 2 may be coded only after local preflight; deployment requires the prescribed SQL Server/WO/costing regression evidence. **No permission to edit costing, posting or execution.**  
**Target:** Existing Product Definition screen and its master-data maintenance service; **no new transaction screens**, **no redesign**, **no modification to stock, costing, WIP, or posting algorithms**.

---

## 1. Executive decision

Keep the current architecture: **Product Definition (versioned company master) → selected ACTIVE definition → Work Order snapshot → Release → Material Issue → Daily Production/WIP → Finished Goods**. An ACTIVE definition revision is immutable; creating a new revision must never auto-refresh or mutate an existing WO. Preserve `SourceProductDefinitionRevisionId`, `SourceBomVersion`, `DefinitionSourceHash`, `SnapshotRevision`, `SnapshotHash`, and all existing event/history semantics.

Deliver exactly two phases:

- **Phase 1 — Master-data integrity:** exact identity, atomic/concurrency-safe revision cloning, narrow routing/readiness validation and error clarity. Keep current consumers and persisted schema intact.
- **Phase 2 — User-friendly entry:** simplify the existing 5-tab entry without changing what it saves; clearer version selection, guided first-time entry, consistent buttons/colours, leftmost list actions, intelligent focus, safe unsaved-change handling, responsive layouts, and business-language validation.

**Crucial qualification:** Phase 1 is *not* zero-impact to future planning: stricter activation checks can reject some new definitions, and intentional edits/activation of a revision can change the materials, machine time and planned cost of **future new WOs** under the **unchanged** calculators. The guarantee is **no edits to costing/posting algorithms, no automatic existing-WO refresh, and no updates to posted stock/WIP/cost**. There is also a **pre-existing BUY→MAKE side effect** in `PrProductDefService.SaveAsync`, discussed in §3A; it must not be mistaken for a regression or a costing-neutral feature. Test it and disclose it, but do not silently change the policy.

**Explicitly deferred:** BUY→MAKE reclassification policy, branch-warehouse sourcing redesign, operational yield below 100%, labour/headcount costing model, alternative-machine execution semantics, non-stock WIP, subcontract costing, process recurrence redesign, WO number generation/prefix, machine-scheduling algorithm, material explosion formulas, and all transaction posting/rollback/replay logic. See §11.

---

## 2. Verified baseline (do not invent missing architecture)

### 2.1 Current relevant files

| Area | Existing file | Repository fact |
|---|---|---|
| Entry | `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor` | New/Edit/View; overview plus `ROUTE`, `OPERATIONS`, `MATERIALS`, `MACHINES`, `LABOUR`; Save Draft, Save & Activate, Cancel, New Version |
| Entry state | `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs` | Nested editors, dirty snapshots, selection/owner switching, lookups, save/clone flows |
| Entry style | `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.css` | Existing component styling; improve rather than replace |
| List | `ErpWeb.UI/Planning/Masters/PrProductDefList.razor` and `.razor.cs` | Uses `CommonDataGridEx`, includes View/Edit or Revise/Explode; compact mobile view |
| Master service | `ErpWeb.Core/Planning/PrProductDefService.cs` | `GetAsync`, `SaveAsync`, `CreateNewVersionAsync`, `ActivateAsync`, lookup/search, deletion blockers, routing validation |
| DTO contract | `ErpWeb.Core/Planning/IPrProductDefService.cs` | Existing product/definition/version and child VMs |
| Master entities | `ErpWeb.Model/Entities/Planning/PrBomHdr.cs`, `PrDefBOM.cs`, `PrBomOperation.cs`, `ProductDefinitionPhase1Entities.cs` | Existing revision, BOM/operation/machine/labour/route-step models |
| DB mapping | `ErpWeb.Model/Configurations/Planning/PrBomHdrConfiguration.cs`, `ProductDefinitionPhase1Configuration.cs` | **Already has** uniqueness for company/product/definition/version, one active revision and one active default (SQL Server filtered indexes) |
| Snapshot boundary — READ ONLY | `ErpWeb.Core/Production/ProductDefinitionSnapshotLoader.cs`, `WorkOrderSnapshotBuilder.cs`, `ProductionWorkOrderService.DraftCommands.cs` | A new WO resolves an active revision and snapshots materials/routes; draft refresh is explicit and fingerprint checked |
| Release boundary — READ ONLY | `ErpWeb.Core/Production/WorkOrderReadinessValidator.cs`, `ProductionWorkOrderService.cs` | Release/readiness validation, statuses and snapshot checks |
| Execution boundary — READ ONLY | `ErpWeb.Core/Production/ProductionMaterialIssueService.*`, `ProductionOutputService.*`, `ProductionFinishedGoodReceiptService.*` | Posting/stock/WIP/cost are downstream and **not editable under this plan** |
| UI pattern | `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor`, `.css` | Reference for left-side row actions and sticky footer/save/cancel visual treatment |
| Existing tests | `ErpWeb.Tests/Planning/Master/PrProductDefServiceTests.cs`, `ProductDefinitionAuthoringTests.cs`, `PrProductDefCentreProjectionTests.cs`; `ErpWeb.Tests/Planning/Transaction/WorkOrderSnapshotBuilderTests.cs`, `WorkOrderReadinessValidatorTests.cs`, `ProductionWorkOrderSqlServerConcurrencyTests.cs` | Extend existing tests instead of adding parallel test frameworks |

### 2.2 Evidence to preserve

1. `SaveAsync` rejects edits to `ACTIVE`, `SUPERSEDED`, `INACTIVE` revisions and uses a header rowversion check when supplied.
2. SQL Server mapping already enforces `(CompanyCode, ProdCode, DefinitionCode, Version)` uniqueness and filtered one-ACTIVE/one-default indexes. **Do not create new indexes/migrations blindly**.
3. `CreateNewVersionAsync` presently computes `Max(Version)+1`, writes header, then clones children using multiple `SaveChangesAsync` calls without one surrounding transaction.
4. The update path of `SaveAsync` first looks up an exact definition/version but can fall back to **product + version alone**; this is unsafe with multiple definition codes for the same product.
5. The activation/routing validation groups final processes by `(WorkCentreCode, OutputItemCode)` while route-step identity includes `StageSequence`. The existing output validation and Work Order readiness contract need careful comparison.
6. `SaveAsync` currently changes an `IvStockMaster` item from `BUY` to `MAKE` even on a draft save. This is a separate cross-module business-policy issue **excluded** here; do not casually modify it while costing is in progress.
7. The company-owned BOM currently stores/validates a branch-scoped preferred warehouse. `PrBomMaterialBranchDefault` exists, but wiring this into actual source selection would change future WO/material issue behaviour. **Deferred**.
8. `ProductDefinitionSnapshotLoader` and `WorkOrderSnapshotBuilder` copy the resolved revision into the WO; `RefreshDraftFromDefinitionAsync` is an explicit user command and checks source revision/hash. Preserve these contracts.
9. The current UI allows one machine and one operator per process-machine through editor logic even though some underlying entities have more expressive fields. Do **not** enlarge execution/costing semantics in Phase 2.
10. BOM-only definitions can exist; current Work Order snapshots require route steps. **Legacy BOM/explosion usage must be preserved** rather than broken by an unconditional activation rule.
11. **Important Phase 2 correction:** `CommonDataGridEx.razor` already creates the action `DxGridCommandColumn` **before data columns** with `VisibleIndex="0"`; a selection column also uses index 0. Per-user persisted layout may override this. The plan must **diagnose** the actual layout before attempting a list action-column fix.
12. Existing `PrProductDefCentreProjection.CentreKey` is `(WorkCentreCode, OutputItemCode)` and does **not** model stage occurrence. Do not broaden that key as an incidental validation fix; repeated-stage authoring is deferred.

---

## 3. HARD SCOPE BOUNDARY — before touching code

**Allowed production files:**

- `ErpWeb.Core/Planning/PrProductDefService.cs` — *only* identity, clone atomicity, narrow structural validation, master-facing messages.
- `ErpWeb.Core/Planning/IPrProductDefService.cs` — *only* additive read-only DTO/lookup/view support if indispensable for UI; avoid breaking signatures.
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`, `.razor.cs`, `.razor.css`.
- `ErpWeb.UI/Planning/Masters/PrProductDefList.razor`, `.razor.cs`.
- **Tests** in existing Planning Master/UI folders. A dedicated Product Definition SQL Server integration test is permitted under existing `ErpWeb.Tests` conventions.
- Existing `PrProductDefCentreProjection` **only if** a proven UI-only selection bug requires a narrowly tested fix; do not expand route semantics or stage identities.

**Read-only dependencies:** all `ProductionWorkOrder*`, `WorkOrderSnapshot*`, `ProductDefinitionSnapshotLoader`, `WorkOrderReadinessValidator`, machine scheduling, `BomExplosionService`, item/UOM/warehouse services, Posting, Inventory, Procurement, Sales and Costing. Read to compare contracts; **do not edit**.

**Forbidden without new explicit authorization:** changes to any `StockLedger`, `CostLayer`, `IvBalLoc`, `ProductionBalLot`, `WipItemBalLoc`, FIFO/WAC/Standard costing, absorbed conversion costing, actual `RequiredQty`/`IssuedQty`/`GoodQty`, posting ledgers, SQL migrations/schema, existing WO snapshots, historical data, month-end/closed periods, future-date guards, posting/rollback/replay methods, permission tables or new master/transaction entities.

**Agent stop rule:** If the apparently simple UI/master change requires any forbidden change, **STOP that subtask, document the dependency and leave it deferred**; do not invent a compatibility shim or alter posting logic.

### 3A. COSTING / QUANTITY FIREWALL — mandatory and highest priority

1. **Do not modify** the existing `PrProductDefService.SaveAsync` material normalization/persistence mapping, `_uom` conversion calls, `PrBomCalc`, `BomExplosionService`, `WorkOrderQuantityCalculator`, `WorkOrderSnapshotBuilder`, machine duration, any costing algorithm or stock ledger. For Phase 1 changes to `SaveAsync`, the permitted hunks are exact-identity/rowversion guard and narrowly scoped *pre-activation structural validation*; **do not refactor whole method**, change `AddRoute`, change `StdQty`, `BaseQty`, `ScrapPercent`, `YieldPercent`, `IssueMethod`, `SupplySource`, `Warehouse`, `BomDefault`, `AlternateGroupCode`, `MachineRatePerHour`, `CostPerOutputUnit`, or any persisted mapping. If a change to these fields is unavoidable, STOP and request a separate plan.
2. **No implicit master reclassification introduced by the enhancement.** Existing code at `SaveAsync` changes `IvStockMaster.MfgType` from `BUY` to `MAKE` on first definition save, even when saving Draft. This is an **existing cross-module behaviour** with possible demand/procurement implications. **DO NOT CHANGE** this policy in Phase 1–2, but show an accurate **pre-save notice** before a user intentionally saves the first definition for a BUY item; do **not** implement an automatic first save, default checkbox acknowledgement that silently saves, or new navigation that commits this action. If the requested guarantee is *no item-master side effects at all*, this policy needs separate authorization and the first-BUY-item save must be excluded from these phases.
3. Exact regression fixture: clone a valid **existing MAKE** definition V1; record source `PrBomHdr`/line/route/machine/labour raw values, `DefinitionSourceHash` and WO `SnapshotHash`, calculated material required/base quantity, planned cycle/minutes, machine/labour planned amount and the resulting posted stock/WIP/cost where test infrastructure permits. For same valid inputs, all values must stay exactly the same after change. Separately test a BUY item and document the **unchanged baseline** reclassification; do not label that test as zero side effects.
4. Freeze source values for all **existing Draft, Released, InProgress and Completed WOs**, plus IP/WI/FG and rollback/reversal facts. Do not call refresh/rebuild/recalculate for existing WOs as a side effect of Product Definition maintenance. New WOs using intentionally changed/newly activated definitions **may have different planned quantities or costs by design**; that is not a calculator regression, and must be tested separately.
5. **Allowed write domain:** Product Definition master tables and current first-save Stock Master behaviour **only**. No transaction-table write at runtime during Phase 1–2. Use test DB before/after comparisons for `ProductionWorkOrders`, material/machine/labour snapshots, stock-balance tables, CostLayer, ledger, WIP pools, posting links, correction facts, reversal and replay facts. No DB backfill, SQL data repair, or test runs against production tenants.
6. The agent must capture `git diff --name-only` **and hunk-level diffs** at each gate. Whitelist alone is insufficient because `PrProductDefService.cs` also contains quantity-sensitive validation, BOM persistence, stock-master reclassification and activation/default logic. Any changed hunk affecting those rules without explicit approval = FAIL.
7. If baseline posting/costing tests fail because of **concurrent unfinished costing work**, do not adjust their expected results or touch costing code. Isolate and report the baseline failure; **mark costing non-regression NOT VERIFIED and block production rollout** until the baseline is stable and an identical before/after fixture can pass.

---

## 4. Phase 1 — Product Definition master-data correctness

### P1-A. Exact Product Definition identity (first and mandatory)

**Where:** `PrProductDefService.SaveAsync` near existing exact header lookup and its product/version fallback. Also check `GetAsync` and all call sites, but do not change read semantics without evidence.

**Implement:**

1. Authoritative update identity is **CompanyCode + ProdCode + normalized DefinitionCode + Version**. Retrieve **exactly** that revision.
2. Remove the unsafe `(CompanyCode, ProdCode, Version)` general fallback. For a legitimate historical blank-code row, **do not modify its persisted identity or linked WO**: requested code must be `STANDARD`, persisted row `DefinitionCode` is null/blank, and exactly one unambiguous legacy revision matches. Otherwise return a clear NotFound/migration message. Before implementing even this legacy lookup, inspect the SQL column nullability and existing migration/tests; **do not create new migration or update historical rows**. Never silently select another named definition.
3. Keep `DefinitionCode` immutable after initial creation; never update a different revision because of a mismatched input.
4. Require a valid existing header rowversion for updates on SQL Server, including the `ActivateAsync` path; do not accept a missing caller token and silently replace it with a freshly fetched token. A missing/stale token must not bypass concurrency checks. Inspect all current call sites and preserve the legitimate UI flow; accommodate SQLite test rowversion setup rather than weakening SQL Server protection. **Do not change global EF rowversion handling**.
5. Preserve tenant scope, Add/Edit permission distinction, active-version immutability and all existing audit timestamps.
6. Guard user-supplied definition codes against silently exceeding 30 characters **at the entry/service boundary**; do not globally change `PrProductDefinitionCodes.Normalize` (shared by downstream consumers).

**Acceptance tests:** two definitions `STANDARD` and `LINE_B` for `FG001`, both version 1. Updating `LINE_B` cannot touch `STANDARD`; an unknown `LINE_C` update fails. Missing/stale token fails. Existing legitimate blank-code legacy STANDARD rows still resolve only through a tested unambiguous path. Wrong company, wrong permission, inactive/immutable version remain blocked. SQL uniqueness remains intact.

### P1-B. Atomic, concurrency-safe Create New Version

**Where:** `PrProductDefService.CreateNewVersionAsync` only.

**Implement:**

1. One transaction owns **source resolution, max-version allocation, new header, route steps, operations, machine options, labour rows and material rows, and commit**. Do not perform any durable child/header write before the transaction begins. Ensure a failed clone leaves **zero** new version rows.
2. Follow the repository's existing EF Core transaction/locking patterns. On SQL Server, ensure safe serialization of version allocation for the exact `(Company, Product, DefinitionCode)` **including the empty/new-key range** (e.g. serializable key-range locking on the existing unique index); retain the unique index as final authority. Confirm EF Core SQL Server execution-strategy compatibility: if configured retries require `CreateExecutionStrategy`, the **entire DB context + transaction + source re-read** must be created/replayed inside the strategy; do not nest ad hoc retry logic. For SQLite use existing test-compatible paths. **Do not introduce DB schema, a stored procedure, global locks or new infrastructure.**
3. Don't introduce a global lock on all products or all tenants. Lock order must be deterministic; keep unrelated definitions independently creatable.
4. On expected unique collision/deadlock/concurrency issues, roll back and report a user-friendly **retry/reload** response. If performing a limited retry, retry the *entire* transaction with a fresh source/version read, never partially reuse entities.
5. The clone must preserve all current user-visible values and relationships: BOM qty/UOM/scrap/default/alternate/supply source/issue method; route-step and operation keys; producer references; machines; labours; WO prefix/remarks. The new version is **DRAFT**, not default, not active.
6. Explicitly test a source whose nested child graph is modified while cloning; do not return a mixed-source revision. Read source header, route/operations/machines/labour, WIP producer references and materials under a **consistent source-read boundary** within the transaction. Detect an intervening source change (e.g. with source header rowversion) or serialize safely. A consistent snapshot or a graceful concurrency failure is required; **never compensate by recalculating material or cost**.
7. Do not rebuild old Work Orders, change existing active revision, reprice material, alter stock master or run production calculations.

**Acceptance tests:** forced exception between header and child inserts => rollback and no orphan; concurrent clone requests => one valid serial outcome per committed version, or one succeeds and one clear conflict (never duplicate version or half clone); all child relationships and `ProducingRouteStepId` remapped correctly; cloned revision has correct audit and permissions; unrelated product clone remains independent. Run SQL Server concurrency tests, not just SQLite.

### P1-C. Narrow, safe activation-readiness consistency

**Where:** `PrProductDefService.SaveAsync` / existing `ValidateOperationsAsync`, no WO/posting file changes.

**Implement:**

1. For **new activation of routed definitions**, add only proven **structural** checks consistent with existing WO readiness: every route-step occurrence must have positive sequence and process membership, exactly one final process **per (work centre, output item, stage sequence)**, and final process last in that occurrence; exactly one terminal FG route-step must produce the parent item. Treat any terminal output UOM requirement as **comparison-only until the current snapshot/UOM contract is proven**—do not rewrite conversions or `BaseUom`, and defer an ambiguous rule rather than changing quantity authority. **Do not run WO readiness/service, scheduling or costing from Product Definition activation.**
2. Correct the final-operation grouping from `(WorkCentre, Output)` to the actual saved route-step occurrence key including `StageSequence`; verify existing UI projection restrictions before introducing test cases with repeated centre/output. **Do not attempt to unlock new repeated-stage authoring behaviour** in this phase.
3. Check existing machine/operation/material owner/producer validation, not duplicate it. Preserve the current required machine/operator and duration-based rules for the applicable process types. Do **not** introduce free-standing labour or new headcount semantics.
4. Preserve **BOM-only activation and existing BOM/explosion callers** unchanged in this scope. A BOM-only definition is permitted for existing non-WO usage, but the UI should show **“BOM-only — cannot create/release a routed Work Order”** as *informational, read-only state*; do not add a new persisted status flag. **Do not introduce a new hard error for BOM-only Save/Activate in Phase 1–2.** Any desire to prohibit future BOM-only activation is a separately reviewed policy change with Sales/Procurement/DR impact tests.
5. Structural validation must finish **before** changing ACTIVE/SUPERSEDED/default state or saving modified child rows. On failure: no target/previous version change, no deleted draft children, no default change and no *new* Stock Master side effect. The **existing BUY→MAKE path must not run before a failed activation leaves the transaction**; verify rollback semantics rather than rewriting the policy. Keep activation/default ordering exactly as it is for valid requests.
6. Prefer narrow reuse of existing **pure structural checks** when possible. Do not move or rewrite WO release logic or production costing/readiness internals simply to avoid a tiny validation repetition.
7. Use clear business messages referencing *route step, process name, and expected correction*, e.g. `Route step 20 (ASSEMBLY) needs one final process before activation.` Avoid `UID`, exception text or internal hash codes in normal UI errors.

**Acceptance tests:** BOM-only Save/Activate remains permitted (but read-only WO warning shown); missing/duplicate/not-last final process for routed activation; same work centre/output at different stages exercised at **service-only** level without claiming existing UI supports authoring repeated occurrences; missing/duplicate terminal FG; terminal output UOM **compared without changing conversion rules**; unchanged machine-duration/material-owner gates; rejected activation leaves original active/default and Stock Master unchanged; valid routed definition activates and creates a new WO with unchanged snapshot for the same source values. Do not change Work Order validator.

### P1-D. Narrow revision lifecycle and deletion regression verification

1. Recheck permissions: Access for reads, Add for create/clone, Edit for save/activate, Delete for delete, and existing disabled/read-only states for Active/Superseded/Inactive. **No new permission codes.**
2. Existing deleted-definition blockers check WO references and parent-definition references; retain and test them. Do **not** broaden cascade deletion. If a concurrent delete/create race needs cross-service locks in WO creation, **defer** rather than changing WO services under this plan.
3. Preserve prior `ACTIVE → SUPERSEDED` and default-selection behaviour; SQL Server filtered indexes must still prevent two ACTIVE versions/defaults. Activation failure must leave prior ACTIVE/default untouched.
4. No data migration or retroactive manipulation of historic revisions, stock balances or activity log.

**P1 EXIT GATE:** exact-identity tests pass; SQL clone failure/concurrency tests pass; compatibility rules explicitly pass; no migration; no forbidden files changed; old active/WO snapshot data unchanged; reviewer signs off Phase 1 before Phase 2.

---

## 5. Phase 2 — User-friendly Product Definition entry (no change to saved business meaning)

### P2-A. Retain one screen; make the path obvious

Keep the existing page, layout infrastructure and five tabs. Do not add a new master or transaction route. The UI should explain the natural path in one glance:

**1 Product → 2 Route/Process → 3 Materials → 4 Machine (when required) → 5 Labour (when required) → Save Draft / Activate**.

- At the top, show **Product**, **Definition code/name**, **Active/default revision**, **Currently editing Vn**, **Draft/Active/Superseded**, and concise **read-only descriptive status**. Distinguish *ACTIVE* from *WO-ready*: show the known “BOM-only, no route” fact, but **do not claim the master UI certifies Release readiness**, which also depends on Work Order/stock/schedule-specific conditions. Do not write `PrBomHdr.ValidationStatus` or introduce a new persisted readiness flag.
- Offer a **Simple setup** presentation for typical small factories: emphasize mandatory sections first, collapse optional/advanced fields and explain them; advanced users can expand them in place. This is **view state only**, not a second business workflow or different calculation mode.
- Keep current route/operation model and mandatory validations intact. For machine-driven steps, don't hide a required machine or operator. For duration-based steps, show the required duration and don't display misleading machine steps.
- Use existing active item, warehouse, work centre, process, machine, operator and WO prefix lookups; show `Code — Description`, never require users to memorize codes. Preserve the existing search/filter plumbing and permission gates.
- Make the **preferred material warehouse** wording accurate and clearly identify it as a preferred issue warehouse, not a stock reservation. Do not make the warehouse optional or change branch selection rules in this phase.
- Put **Standard output quantity**, BOM material **Std Qty**, output UOM, machine timing and scrap near their meaning with small inline help; do not change rounding, precision or conversion.
- Show an **explicit, pre-commit** explanatory notice for any `BUY` item before the first definition save: current service **does reclassify** it to `MAKE`, including Draft Save. Do not silently auto-save, update `MfgType` directly from the UI, or change the existing policy in this plan. Include a BUY-item regression test documenting exactly the existing write; if this is unacceptable to the business, **block this use case from the release scope and request a separate approved fix**.

### P2-B. Reduce clicks without silently losing nested editor changes

- Keep existing child editors (`CentreEdit`, `OperationEdit`, `Line*`, `MachineEdit`, `LabourEdit`) and their authoritative validation. Reuse their methods; do not create a parallel set of save routines.
- After adding a route step, focus/select the new step and offer a clear **Add Process** next action. After saving a process, retain the selected operation and show **Add Material / Assign Machine** next actions with one click. After saving a machine, show the labour next action only when required.
- Preset only safe, deterministic values already present in the selected product/process/master data (UOM, description, existing default warehouse, permitted process-specific machines); do not fabricate rates, machines, labour, scrap, quantities or dates. No hidden silent auto-posting.
- **Critical:** `IsDirty` compares the saved `Model`, while nested editors have their own `IsChildEditorDirty` snapshots. For Save Draft / Save & Activate / Cancel / switching root/definition/route/tab, detect both **and** any pending material-line form (`Line*` fields) not yet materialized. **Do not silently commit a nested editor**: offer **Apply current editor / Discard current editor / Stay here** (where applicable) and run its *existing* validator before applying; never save invalid child rows, change `Model.Operations` ordering/identity automatically, or send partially edited data to `ProductDefs.SaveAsync`. If dialogs cannot be implemented safely within page scope, do not implement auto-navigation; keep user on the editor with a clear message.
- Use one user-friendly validation summary with focus/tab navigation to the first field. Field messages should identify the user-visible stage and correction. Preserve server-authoritative validation and `TextExpression`/`ValueExpression` for DevExpress components that require them.
- Never auto-activate merely because users pressed Save Draft. Never auto-create a revision when selecting a tab; retain current explicit New Version action.

### P2-C. Standard actions and visual treatment

Use **`IvMiscReceipt.razor`** and related CSS as the visual standard, but keep the Product Definition's distinct master statuses:

- Sticky footer: **Cancel** (secondary) / **Save Draft** (secondary) / **Save & Activate** (primary); show only actions allowed in the current mode/status/permission; disable all writes while `IsSubmitting`.
- View mode: **Close**, **Explode BOM**, and **New Version** only if Add permission. An ACTIVE definition must never show ordinary Edit.
- **First verify, do not assume a missing feature:** current `CommonDataGridEx.razor` already defines `DxGridCommandColumn VisibleIndex="0"` ahead of data columns (and the selection column also uses 0); Product Definition uses it with `GridKey="pln-product-def-list"`. Test clean layout **and an existing persisted per-user layout**. If actions are already left, **make no code change**. If saved layout moves them right, test the existing **Reset Layout** command and document a safe per-user remedy; do not silently clear users' saved layouts, change `GridKey` to force a reset, or edit shared grid components. Mobile compact card should offer visible View and allowed Edit/Revise actions without scrolling, using existing event/permission paths.
- Reuse consistent status chip colours: Draft (neutral), Active (positive), Superseded/Inactive (muted), validation warning (amber), error (red). Do not invent a new theme or overwrite global CSS.
- Standardize input widths and alignment; narrow code/sequence/qty/UOM/cost fields, generous descriptions, labels not truncated. Avoid massive full-width text boxes for short codes.
- Test desktop 1920/1440/1280, tablet 1024/768, phone 375–430 px; footer buttons wrap correctly and never cover fields; long descriptions wrap reasonably; tree/grid horizontal scrolling affects **data** not access to primary actions.

### P2-D. Versions, status, and safe navigation

- Clearly separate **Active version** and **Latest version** in the list. Existing list `BomStatus` is an alias for *latest* status, not necessarily active; label it **Latest status** to prevent confusion.
- `VIEW` should clearly show whether it opens latest or active. An optional **read-only Active / Latest** selector may use existing `GetAsync(..., version)` **only if exact version navigation is retained**; existing entry routes contain no version segment. Prefer a local read-only preview or explicit URL query parameter with correct reload/dirty state rather than changing shared WO definition lookup. If robust exact-version navigation cannot be proven in allowed UI files, defer the selector and show clear current version/status text instead. Never silently switch the saved definition for WO creation.
- `EDIT / REVISE` should present **Edit Draft** when latest is Draft, and **Create New Version** when the selected version is immutable. Preserve Add/Edit permission semantics. **Do not automatically clone from Active merely because Latest is Draft**, or clone from Latest when the user explicitly selected Active; preserve exact source version selected by the user and verify clone identity. No automatic change of Default/ACTIVE status.
- When a new draft is cloned, show `Created Draft Vn from Vm` and navigate to that exact revision if possible; never silently use a different definition code.
- Support clear cancel/unsaved-change confirmation, maintain selected route/process when practical, and retain parent/child breadcrumb. Do not erase user input after a validation failure.

### P2-E. Usability verification from an ERP clerk's perspective

Manually exercise: brand-new single-product definition; editing Draft; creating V2 from V1; changing the default; adding two processes and one component; adding machine + required operator; selecting an alternate material; viewing a lower-level subassembly; navigating away with unsaved child editor state; activation with missing final process; two versions with the same product; permission-limited View-only. Count clicks and verify the main actions do not need horizontal scrolling. **No invented machine/resource or costing values**.

**P2 EXIT GATE:** all existing saved DTO fields round-trip unchanged; no changes in quantity/UOM formulas; UI works at specified widths; correct actions/permissions/statuses; no hidden unsaved data loss; no global CSS or grid regression; reviewed screenshots (or browser UI tests) and user acceptance.

---

## 6. Implementation order and phase gates (do NOT combine into one giant change)

| Gate | Deliverable | Mandatory proof | Go / No-Go |
|---|---|---|---|
| 0 | Baseline audit | Current working tree and commit, local modifications, field/DB mappings, test baseline, and diff whitelist | **STOP** if master is concurrently being changed without coordination |
| 1A | Exact identity fix | Cross-definition + legacy+ permission tests | No change to other definition/revisions |
| 1B | Clone atomicity/concurrency | Forced failure; 2-user SQL Server clone collision; unchanged active revision | Zero partial revisions |
| 1C | Narrow activation/readiness | Route occurrence tests, legacy BOM-only acceptance, prior active remains valid on failure | No changes to WO validator or execution |
| 1D | Master regression | Company isolation, immutability, delete blockers, SQL unique indexes | Phase 1 signed off |
| 2A–B | Guided UI + dirty editors | New/Edit/view flows, server validation, no editor data loss | No backend calculation changes |
| 2C–D | Visual/layout + versions | Left actions, standard footer, permissions, width/mobile, exact version navigation | No global component regression |
| 2E | UAT | Manufacturing clerk and admin journeys | Phase 2 signed off |
| Final | Diff + regression report | File whitelist, test results, before/after critical data snapshots | Release only if all required checks pass |

**Agent must report at each gate:** files changed, exact defect fixed, old behaviour preserved, tests run and results, untested risks, and why the change does not touch costing/posting. Never claim a gate PASSED unless the tests actually ran.

---

## 7. SQL Server and automated test requirements

Extend the existing tests before broad code changes:

- `PrProductDefServiceTests`: cross-definition identity collision, stale/missing rowversion including `ActivateAsync`, code length, Add/Edit/Delete permission matrix, active immutability, legacy blank-code compatibility, clone deep copy, failed clone rollback, unchanged current active/default, and **existing BUY→MAKE on Draft Save unchanged** (a documented pre-existing side effect, not a new enhancement).
- `ProductDefinitionAuthoringTests`: per-route-occurrence final validation, terminal FG requirements, material operation association, WIP producer consistency, no change to approved UOM conversion and machine timing gates, BOM-only compatibility.
- `PrProductDefCentreProjectionTests`: current UI mapping still stable; no route identity widened by UI cleanup.
- `ProductDefinitionSnapshotLoaderTests`, `WorkOrderSnapshotBuilderTests`, `WorkOrderReadinessValidatorTests`: **unchanged outputs** when using the same valid definition and WO planned qty; cloned revision only selected for new WO after activation.
- **SQL Server integration** (using existing SQL Server test host): two simultaneous clones for one definition, two simultaneous activations, duplicate-version unique index, injected failure halfway through cloning, same product with two definition codes + same version, EF rowversion rejection, rollback of failed activation. SQLite unit tests alone are **insufficient** for SQL Server filtered indexes and lock semantics.
- Existing `ProductionWorkOrderSqlServerConcurrencyTests` / production posting tests: run relevant regression tests to establish that snapshots/posting permissions and existing commit guards remain unchanged; **do not edit production code to make tests pass**.
- UI tests/static contract: dirty nested editors including pending material form, disabled actions, **clean and persisted grid layout** position (not just markup), footer, DevExpress binding expressions, authorized buttons, explicit BUY→MAKE notice before first Save, no accidental activate on Draft Save, and no silent selection of another revision.

Suggested commands from repository root (adjust filter to actual test names after inspection):

```powershell
# Baseline and scope
git status --short
git diff --name-only

# Build
dotnet build ErpWeb.slnx -c Release

# Planning master tests
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --filter "FullyQualifiedName~PrProductDefServiceTests|FullyQualifiedName~ProductDefinitionAuthoringTests|FullyQualifiedName~ProductDefinitionSnapshotLoaderTests|FullyQualifiedName~PrProductDefCentreProjectionTests"

# WO compatibility tests
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --filter "FullyQualifiedName~WorkOrderSnapshotBuilderTests|FullyQualifiedName~WorkOrderReadinessValidatorTests|FullyQualifiedName~ProductionWorkOrderSqlServerConcurrencyTests"

# Then run the full suite allowed by the environment; document every skipped / non-runnable SQL Server test.
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release
```

**SQL Server integration prerequisites:** use a dedicated test DB, ensure the project's test fixture opts in appropriately, don't execute against a customer/production tenant, don't claim skipped SQL Server tests as passed. If the test DB/SQL Server test infrastructure is unavailable, mark Phase 1 clone-concurrency gate **NOT VERIFIED** rather than APPROVED.

---

## 8. Invariant / downstream non-regression matrix

For each case record **before and after** against the same test fixture or safe snapshot:

| Scenario | Must remain true |
|---|---|
| Existing WO created from ACTIVE V1; definition V2 created | V1 WO `SourceBomHdrId`, source revision, materials, route, `SnapshotHash`, planned qty, WIP and issue balances unchanged |
| Existing Draft WO | V2 does not refresh it automatically; refresh/change requires explicit current command and fingerprint validation |
| Existing Released/InProgress/Completed WO | Product Definition work never rebuilds it or mutates any child row |
| IP New/Post/Rollback | Existing material quantity/stock/FIFO/CostLayer/posting behaviour unchanged |
| Daily Production (WI) Post/Rollback | Existing Good/Reject/Scrap/WIP quantities and absorbed cost unchanged |
| FG Post/Rollback | Exact transferred value and quantity-conservation rules unchanged |
| Production Stock Return/Adjustment/WO transfer | Posting, reversal, stock/WIP valuation and history unchanged |
| Procure/Sales upstream/downstream | BOM explosion and transaction linkage unchanged; no new lookup contract that breaks them |
| Closed period, future dates, backdated replay | All original transaction guards unchanged; no new bypass |
| Two users on same definition | No cross-definition update, duplicate revision or partial clone |
| Security/audit | Company/branch isolation, Add/Edit/Delete, created/modified/activated data unchanged apart from requested master action |

**No calculation drift allowed:** For identical source definition/revision, planned quantity, UOM mapping, rate configuration and posting fixture, material standard/required/base quantity, scrap, cycle count, setup/queue/cycle duration, planned machine/labour amounts, and stock/WIP/posted values must be numerically identical before/after (compare canonical decimal values and hashes as applicable). For **a deliberately changed new ACTIVE definition**, future-WO planned values may change legitimately: do not compare unlike inputs. Never “fix” a costing test by modifying execution code or test expectations within this plan. **Missing/unstable baseline = NOT VERIFIED and no production rollout.**

---

## 9. Deployment and rollback

- **No schema migration, no data backfill, no re-posting, no recalculation, no forced WO refresh.**
- Deploy Phase 1 independently, after SQL Server compatibility tests; then deploy Phase 2.
- Capture a before/after inspection of representative `PrBomHdr`, `PrDefBOM`, `PrBomRouteStep`, `PrBomOperation` rows and released WO snapshot fingerprints in a **test database**. Do not query customer data without authorization.
- Application rollback: revert only the approved Product Definition master/UI commits. Do not undo or rewrite user-created revision rows, not even if a draft was created with the new code; those are legitimate master records. If a newly stricter validation blocks a necessary valid legacy workflow, revert the validation change, not the whole production module.
- Keep both phases in separate, reviewable commits. Agent must not reset or overwrite somebody else's uncommitted costing work.

---

## 10. Definition of DONE — strict acceptance

All must be true:

1. Company/Product/DefinitionCode/Version is always resolved exactly for update; no fallback to another definition.
2. New-version clone is atomic, SQL Server concurrency-safe, fully relationally intact and never creates partial drafts.
3. Routed definitions are checked against their own route-step occurrences; valid future WOs still build using the unchanged snapshot algorithm.
4. Legacy BOM-only use is preserved; not-WO-ready status/warning is explained, never misrepresented as WO-ready.
5. Existing Active, Superseded and released WO records are unchanged; no background or automatic refresh.
6. UI retains the same saved master fields and semantics; ordinary clerk can complete a basic definition with sensible prompts/lookups and without unnecessary navigation.
7. Action buttons are accessible at the left of the list; footer/actions/colors align with the existing Misc Receipt convention; short fields aren't oversized; mobile usable.
8. Pending child edits, including **unapplied material-line and nested machine/labour forms**, never disappear silently when saving, switching tab/owner/version, or cancelling; no implicit editor commit or silent BOM ordering/identity change.
9. Permissions remain unchanged; a disabled UI action is also denied by the server.
10. **No** changes to posting, costing, stock, WIP, work-order execution, source schemas, backdated replay, closed periods or future-date rules; **no hunk-level changes** to BOM quantity/UOM/scrap, machine/labour rate, item-master reclassification or snapshot-material mapping inside the allowed service.
11. Targeted tests, SQL Server concurrency tests, unchanged-definition WO snapshot **and posted stock/WIP/cost** regression tests, and UI UAT pass; any unavailable test is explicitly **NOT VERIFIED**, so **deployment is not approved**.
12. Final diff includes only whitelisted files; any other file requires an explicit user-approved amendment before implementation.

---

## 11. DEFERRED / OUT OF SCOPE — do NOT quietly implement

These may be valuable for Malaysian SMEs, but they can change real transactions or future quantity/cost authority:

1. **`BUY → MAKE` on draft save:** existing `SaveAsync` mutates `IvStockMaster.MfgType`, which can affect upstream demand/procurement selection. **Do not change the policy or Stock Master in Phase 1–2.** Surface the current behaviour honestly; make a separate reviewed change proposal later.
2. **Branch-specific BOM warehouse defaults:** `PrBomMaterialBranchDefault` exists but is not integrated into source selection. Do not wire it into WO material snapshots now.
3. **True simple manufacturing without routing:** WO currently requires route steps. UI guidance may help configure one minimal real process, but must not bypass WO release/readiness.
4. **Multi-operator/headcount/hourly labour cost:** current UI/service mostly use legacy `CostPerOutputUnit`; snapshot treats some `CostBasis` options as unsupported. No new cost models, rates or calculations here.
5. **Non-stock WIP/yields/partial output:** route step `OutputType` currently derives from output product match; WO readiness and Daily Production check `YieldPercent == 100`. No new types/yields without reviewing quantity and costing flows.
6. **Repeated route stages / alternative machine selection / subcontract operations:** may require changing `PrProductDefCentreProjection.CentreKey`, route occurrence identity, schedule and WIP behaviour. Out of scope; service-level validation must not be misrepresented as new UI authoring support.
7. **Work Order prefix:** definition has a Prefix field, while WO number allocator currently produces `WO########`. Do not change numbering in this plan; if necessary, clarify in UI that the current WO number format is system-controlled.
8. **Delete/create races across services:** do not change WO creation or locking protocols in this plan; document a follow-on transaction-consistency task if tests prove the risk.
9. **Costing/FIFO/replay enhancements in progress:** absolutely out of scope. A passing test from before an active costing change is not valid before/after proof; use the same frozen baseline and code window.
10. **List action placement:** shared grid action column is already left in source; investigate user-persisted layout. Do not introduce a global grid refactor or auto-delete saved layouts just to achieve cosmetic placement.

---

## 12. Mandatory final AI-agent output

The agent's final report must include: **(a)** current branch/commit and local status, **(b)** files changed and diff summary, **(c)** Phase 1 vs Phase 2 completed items, **(d)** every test executed with outcome, **(e)** SQL Server concurrency confirmation or explicit unverified status, **(f)** before/after WO/stock/WIP/cost non-regression evidence, **(g)** remaining issues/deferred tasks, **(h)** screenshots of the revised UI at desktop and mobile if browser testing is available, **(i)** an explicit statement whether any forbidden file was modified, and **(j)** PASS/FAIL/NOT VERIFIED for each gate.

**FINAL RULE:** **Do not approve the implementation just because the plan is labelled controlled.** Reject final acceptance if source invariants are compromised, SQL Server concurrency/cost regressions remain unverified, user actions lose data, new edits silently mutate `IvStockMaster` beyond the documented legacy path, or quantity/cost values drift for identical inputs.

---

## 13. Strict reviewer addendum (31 Oct 2026) — costing non-regression verdict

**Review reference:** branch `productionv2` commit `2b348746c40055aa322bd07d3f15d46b0bd35e32`. Inspected actual current GitHub source for `PrProductDefService`, `PrProductDefCentreProjection`, `PrBomHdrConfiguration`, `ProductDefinitionPhase1Configuration`, `WorkOrderSnapshotBuilder`, `ProductionWorkOrderService.DraftCommands`, `ProductionOutputService.Posting`, `CommonDataGridEx` and the Misc Receipt UI reference. The implementation agent must still recheck **current local** files and uncommitted costing changes.

**Review conclusion:** The **intended changes do not need any costing/posting algorithm modification**. The original plan, however, had four avoidable exposure points, corrected in this version:

1. **Pre-existing master side effect (High):** saving the first BUY product definition calls `product.MfgType = MAKE`, even for Draft. A guarantee of *no data impact whatsoever* would be false. Preserved unchanged, surfaced explicitly to users, and isolated in tests. Separate policy approval needed to change it.
2. **Master method is quantity-sensitive (High):** the same allowed `SaveAsync` method persists BOM qty/UOM/scrap/supply source and machine/labour rates. An allowed-file whitelist alone is not sufficient; this plan now mandates a strict **hunk-level firewall** and prohibits all mapper/math edits.
3. **Validation can alter future WO eligibility (Medium):** activation structural gates may reject invalid new routings but must not change any WO's stored snapshot, quantities or posting, and **BOM-only activation stays compatible**. WO release rules remain read-only.
4. **UI grid actions may need no fix (Low):** `CommonDataGridEx` already has left-side `VisibleIndex=0`; the issue may be persisted layout. Do not change the global component or delete user layouts based on an incorrect assumption.

**Costing-safety sign-off:** **Design-reviewed / conditional**, not executable proof. No tests were executed in this review environment. The agent may implement the bounded master/UI changes but **must not claim production-ready, costing-safe or 10/10 until SQL Server + unchanged-input snapshot + IP/WI/FG/costing/rollback regression gates pass against the current local branch**.

