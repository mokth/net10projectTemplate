# ProductionV2 — Per-Family Lookup Maintenance Menus
## Repository-verified, corrected AI Coding Agent implementation plan — v3

**Review date:** 28 October 2026 (Asia/Kuala_Lumpur)  
**Repository / branch:** `mokth/net10projectTemplate` / `productionv2`  
**Reviewed exact HEAD:** `d7a744439490d2c63863d96a59dc277c4c3cab4a` (2026-10-28 02:57:16 UTC).  
**Original plan:** `per-family_lookup_menus_2bec1366.plan.md`  
**Previous review v2:** SUPERSEDED. The user's new commit introduced the lookup foundation, so the old “foundation absent” finding is no longer true.  
**Status:** **REVISED — IMPLEMENTATION-READY SUBJECT TO G0; NOT APPROVED FOR UNCONDITIONAL DEPLOYMENT.**  
**Assessment of original plan at this HEAD:** **7.5/10** (valid architecture and now-existing foundation, but real security, runtime, deployment, and test gaps).  
**Assessment of revised plan:** **9.6/10 design readiness**; **do not claim end-to-end 10/10 or a green build without execution and DB preflight**.  
**Authority:** current repository code; plan line numbers are hints only. If HEAD changes, re-check relevant files before editing.

---

## 0. Objective, exact scope and source-of-truth finding

Provide **five independent, least-privilege Master menu entries** for the already-implemented, company-scoped `AdLookupValue` classifications, retaining the **existing `/admin/lookups` full-family maintenance page**. Reuse the current service, registry, UI markup/design, tenant scope, optimistic concurrency, assignment rules, and menu deployment patterns. No stock ledger, costing, posting, rollback, production scheduling, accounting logic, or schema redesign is authorized by this menu task.

**Five immutable server-owned mappings** (NOT client-configurable):

| Lookup family | Module / parent | Menu code | Literal route | Visible title / order |
| --- | --- | --- | --- | --- |
| `WAREHOUSE_TYPE` | Inventory / `INV_MASTER` | `INV_WAREHOUSE_TYPE` | `/inventory/lookups/WAREHOUSE_TYPE` | Warehouse Types / 8 |
| `MACHINE_TYPE` | Planning / `PLN_MASTER` | `PLN_MACHINE_TYPE` | `/planning/lookups/MACHINE_TYPE` | Machine Types / 16 |
| `WORK_CENTRE_CLASS` | Planning / `PLN_MASTER` | `PLN_WORK_CENTRE_CLASS` | `/planning/lookups/WORK_CENTRE_CLASS` | Work Centre Classes / 17 |
| `SUPPLIER_TYPE` | Purchase / `PO_MASTER` | `PO_SUPPLIER_TYPE` | `/purchase/lookups/SUPPLIER_TYPE` | Supplier Types / 7 |
| `SUPPLIER_SUBGROUP` | Purchase / `PO_MASTER` | `PO_SUPPLIER_SUBGROUP` | `/purchase/lookups/SUPPLIER_SUBGROUP` | Supplier Subgroups / 8 |

**Keep the reserved three `CUSTOMER_*` registry records non-maintainable**; no menus, no default values, no migration of existing customer codes.

### Verified from current HEAD (not speculative)

- **Present now:** `ErpWeb.Model/Entities/AdLookupValue.cs`, `ErpWeb.Model/Configurations/AdLookupValueConfiguration.cs`, `AppDbContext.AdLookupValues` (line 131), `ErpWeb.Core/Admin/AdLookupTypes.cs`, `AdLookupTypeRegistry.cs`, `AdLookupAssignments.cs`, `AdLookupDefaultsSeeder.cs`, `IAdLookupService.cs`, `AdLookupService.cs`, `AdLookupModels.cs`.
- **Present now:** `ErpWeb.UI/Admin/AdLookupList.razor` and `.razor.cs`; admin route `/admin/lookups`, `MenuCodes.AdminLookups`, admin entry in `menus.xml`; DI `AddScoped<IAdLookupService, AdLookupService>()` in `CoreServiceCollectionExtensions.cs` (line 157).
- **Present now:** five `UserMaintainable: true` families in `AdLookupTypeRegistry` with actual consumer max lengths: Warehouse 20, Machine 30, Work Centre 10, Supplier Type 20, Supplier Subgroup 20; three `CUSTOMER_*` records set false. Warehouse defaults defined in the registry; seeder uses the maintainable list.
- **Present now:** `AdLookupService` has the exact four maintenance methods `ListForMaintenanceAsync`, `GetAsync`, `SaveAsync`, `SetActiveAsync`; they currently check only `MenuCodes.AdminLookups`. Assignment reads and validation are separate methods and should not be gated by lookup-maintenance rights.
- **Present now:** `ErpWeb.Core/Menus/MenuSyncService.cs` creates *ACCESS applicability* for new XML menus but does **not** auto-create ADD/EDIT applicability; it soft-disables rows absent from shipped XML. `AccessRightService.CanAsync` requires ACCESS before non-ACCESS actions for normal users (ADMIN/SYSTEM_ADMIN bypass is a separate existing rule).
- **Not present at this HEAD:** the specific `scripts/alter-ad-lookup-value.sql` required by the committed generic lookup foundation plan (`plans/productionv2_GENERIC_CONFIGURABLE_LOOKUP_MASTER_FINAL_AI_AGENT_PLAN_BIZ_LOGIC_SAFE_REVIEWED.md`, §24); **not present:** a dedicated `ErpWeb.Tests/Admin/AdLookupServiceTests.cs` or any `*AdLookup*` test file in the currently committed test tree. This is a **deployment/test evidence gap**, not proof the live database lacks a table.
- **Not present:** any of the five new per-family menu constants / XML entries, or five family routes. Therefore the *per-family menu feature* has not been implemented in this HEAD.
- `AdLookupList.razor` line 76 currently uses a **read-only** `DxTextBox Text="@EditDefinition?.DisplayName"` without an explicit `TextExpression`. Given the repository's DevExpress textbox-binding requirements, **verify this on a live render and eliminate the unbound-Text runtime error during extraction**; do not blindly preserve problematic markup.
- The latest foundation commit also changed unrelated stock replay / FIFO / WAC / costing files. **Treat them as unrelated working code; do not expand this menu task into those modules**.

## 1. G0 — Preflight / hard deployment gate (must report evidence)

Before coding, re-fetch HEAD and inspect all current files listed in §0. Record their actual contents and signatures. The five family menu pages **must not** become the vehicle for silently rebuilding the lookup foundation. In particular:

1. Confirm `dbo.AdLookupValue` is installed in **each target company database** using the *approved* foundation deployment (`scripts/alter-ad-lookup-value.sql` is not committed in this HEAD). Verify columns, `RowVersion`, company/type/code unique index, maximum lengths, and active/audit fields against `AdLookupValueConfiguration`; do this read-only first.
2. If there is no installed table/approved script, mark **G0 FAIL / DEPLOYMENT BLOCKED**. Restore/commit and independently verify the approved foundation DBA script (per the existing foundation plan), **as a separate controlled prerequisite**. Do not invent arbitrary DDL in this five-menu change or assume EF migrations run automatically.
3. Verify `ADMIN_LOOKUPS` exists in `dbo.Menu` and has active `ACCESS/ADD/EDIT` `MenuPermission` applicability where deployed. **Source presence in XML is not proof of production database initialization**.
4. Confirm `/admin/lookups` works for its intended admin role on a representative DB; add/view/edit/deactivate/reload and check concurrency. Verify the existing assignment dropdowns and consumer validation still work.
5. Confirm that the existing `MenuSyncService` startup sync occurs before any role grants are evaluated, and deployment sequencing matches the actual hosting process. Never auto-grant all five menus to every role.
6. Existing tests for the lookup foundation are absent by filename at this HEAD. Add focused baseline lookup-service tests *as part of the test gate*, or surface blockers; do not claim they “pass unchanged.”

**Output gate log:** foundation DDL verified? admin menu applicability verified? live admin page smoke tested? current HEAD recorded? If any fails, implementation may be reviewed but **must not be deployed**. No change to production data or stock postings during these checks.

## 2. G1 — Registry metadata / menu constants (single trusted mapping)

**Files:** `ErpWeb.Core/Menus/MenuCodes.cs`; `ErpWeb.Core/Admin/AdLookupTypeRegistry.cs`; tests.

1. Declare exactly the five constants in the mapping table, with their exact uppercase strings; do not alter any existing constants.
2. Extend the **existing** `AdLookupTypeDefinition` positional record by **optional trailing** members, preserving constructor compatibility:

   ```csharp
   string? MaintenanceMenuCode = null,
   string? Route = null
   ```

3. Set both fields explicitly for the five maintainable families only. Preserve all current `Code`, `DisplayName`, `Module`, `MaxCodeLength`, `DefaultValues`, and `UserMaintainable` values. Do not change Warehouse seed values or machine/centre/supplier seed behavior.
4. Keep three reserved customer families at `UserMaintainable: false`, `MaintenanceMenuCode: null`, `Route: null`.
5. Treat `MaintenanceMenuCode` as a **mandatory non-null allowlisted property for any maintainable family** at authorization time. Do not default a missing mapping to `ADMIN_LOOKUPS` — that masks misconfiguration.
6. Add a registry parity test: maintainable set has five unique families, unique nonempty menu codes/routes matching table, routes prefixed by correct **lowercase** module path (case-insensitive test for `Module` label), no other maintainable family missing mapping; customer metadata remains disabled.

## 3. G2 — Server authorization (the true security boundary)

**File:** `ErpWeb.Core/Admin/AdLookupService.cs`; documentation `IAdLookupService.cs`; tests.

### 3.1 The only permission policy

```csharp
private async Task<bool> CanMaintainAsync(
    AdLookupTypeDefinition? definition,
    string permissionCode,
    CancellationToken ct)
{
    if (definition is not { UserMaintainable: true }
        || string.IsNullOrWhiteSpace(definition.MaintenanceMenuCode))
        return false;

    return await _access.CanAsync(definition.MaintenanceMenuCode, permissionCode, ct)
        || await _access.CanAsync(MenuCodes.AdminLookups, permissionCode, ct);
}
```

This is pseudocode illustrating authority; compile against actual signatures. `ADMIN_LOOKUPS` is deliberately **broad full-family permission** for appropriately privileged grants, **not** a grant to every module user. Do not accept a menu code supplied by UI/request. Preserve `AccessRightService` normal requirement for ACCESS before ADD/EDIT; never replace it with ad hoc role-name checks.

### 3.2 All four maintenance methods, exact order

- **`ListForMaintenanceAsync(lookupType, includeInactive, ct)`**: confirm authenticated `TryCompanyScope()`, resolve the caller-supplied lookup type against `AdLookupTypeRegistry`, require `UserMaintainable`, then authorize **ACCESS for that definition**, THEN execute existing EF query with `CompanyCode + LookupType` filters. Unauthorized/unknown list stays empty. Do not enumerate or fetch all family rows first.
- **`GetAsync(uid, ct)`**: confirm company scope, query **`Uid` AND `CompanyCode`**. Resolve the **persisted** `entity.LookupType` to its definition and `UserMaintainable`. Require **ACCESS for the persisted family**. Unknown, non-maintainable, unauthorized, missing or foreign-company rows return indistinguishable `IvMasterErrorCode.NotFound` (no disclosure of another family/tenant). Return existing edit DTO exactly as today when authorized.
- **`SaveAsync(model, isNew, ct)` / NEW**: validate non-null model and tenant; resolve and validate its registry family; authorize **ADD for that family**; preserve existing code normalization, family max length, description validation, uniqueness, duplicate-key handling, timestamps/actor, and DB-concurrency semantics.
- **`SaveAsync(model, isNew, ct)` / EDIT — SECURITY-CRITICAL ORDER:** confirm tenant; **load row by persisted UID AND company first**, resolve **persisted family**, authorize **EDIT on persisted family before disclosing existence/details or family mismatch**. Only then verify `model.LookupType` equals persisted type, `model.Code` is unchanged, `RowVersion` matches, and other existing validation. This avoids the original plan's permission-on-*user-supplied-family* pattern, which could disclose another family's row and is unsafe if subsequent checks are reordered. Unauthorized/foreign company rows return NotFound. Do not let a permitted `WAREHOUSE_TYPE` editor mutate `MACHINE_TYPE` via a forged DTO.
- **`SetActiveAsync(items, isActive, ct)`**: verify tenant, nonempty IDs, no duplicate/conflicting tokens, all rows loaded by `CompanyCode`, row-count exact, all persisted families known + maintainable, **EDIT on EACH distinct persisted family**, all row versions valid; **only then mutate and save**. If any row unauthorized, stale, missing or reserved, reject the **entire batch**. Keep existing single SaveChanges transaction semantics; verify relational-provider rollback on mid-batch concurrency; add explicit transaction only if existing guarantee is inadequate. Deactivation must not delete rows, reassign consumer codes, backdate transactions or alter history.
- **`ListForAssignmentAsync`, `ListAsync`, `ValidateAssignmentAsync`, `EnsureLookupDefaultsAsync`**: **no permission change**. Do not add maintenance permission to assignment APIs or change consumer code rules. Keep existing company scope. Update `IAdLookupService` documentation only to reflect the new permission policy.

### 3.3 Threat cases to explicitly test

1. A user with `PO_SUPPLIER_TYPE.ACCESS + EDIT` cannot GET, UPDATE, ACTIVATE or DEACTIVATE a `MACHINE_TYPE` UID (including when forging `LookupType = SUPPLIER_TYPE`).
2. A user with only `INV_WAREHOUSE_TYPE` can list/save warehouse only; cannot see other modules/families.
3. `ADMIN_LOOKUPS` with appropriate ACCESS/ADD/EDIT can maintain all **five** families through the existing admin page/service, never the three reserved customer families.
4. ADD without ACCESS and EDIT without ACCESS fail under **actual `AccessRightService` effective-right semantics**. A mock returning true for ADD alone does not model this rule; add a separate rights integration test or configure realistic rights.
5. Explicit DENY within a menu, revoked grants, `MustChangePassword`, ADMIN/SYSTEM_ADMIN bypass, tenant isolation and permission cache refresh follow existing implementations; do not extend bypass rules.
6. Two records selected across families: allowed only when the user has EDIT on **both**; otherwise all-or-nothing. Mixed company IDs cannot write.

## 4. G3 — Extract the existing screen to **one** reusable UI

**Files:** extract current `ErpWeb.UI/Admin/AdLookupList.razor[.cs]` into `ErpWeb.UI/Components/Common/Lookups/AdLookupMaintenance.razor[.cs]`; preserve the admin route as a thin wrapper. Make only the specified extra UI fixes.

### 4.1 Shared component contract

Use these or equivalent unambiguous parameters:

```csharp
[Parameter, EditorRequired] public string MenuCode { get; set; } = string.Empty;
[Parameter] public string? LookupType { get; set; }      // fixed on module wrappers; optional for admin
[Parameter] public string Eyebrow { get; set; } = "Admin";
[Parameter] public string Heading { get; set; } = "Lookup Values";
[Parameter] public bool ShowFamilySelector { get; set; }
```

1. Do not duplicate list/grid/edit/activation logic across module pages. The existing `AdLookupList` uses a `PageBase` class; shared child can inherit `ComponentBase` and must be rendered only inside an authorized routable wrapper. Admin wrapper **keeps** `@page "/admin/lookups"`, `@inherits PageBase`, `PageTitle`, `MenuAuthorize MenuCodes.AdminLookups` and the existing selectable five-family behavior.
2. For a module wrapper, **bind its fixed, known family**, no family selector. Validate the registry mapping and menu code inside the shared component defensively; mismatch => no query/no operation.
3. Replace each UI `AccessRights.CanAsync(MenuCode,...)` call (including `CanEditFromView`, `EnsurePermissionAsync`, toolbar/row actions, switching view-to-edit) with the **specific component-supplied MenuCode**. The server will independently authorize persisted family. Do not use `AdminLookups` to enable module action controls.
4. Keep the existing styling and UX pattern (`iv-page`, `iv-hero`, `iv-eyebrow`, `CommonDataGridEx`, `DxPopup`, `DxFormLayout`, `EditForm`, NEW/ACTIVATE/DEACTIVATE/REFRESH and VIEW/EDIT), no DELETE. Field labels stay descriptive, code input uses `MaxCodeLength`; actions respect ADD/EDIT independently and `IsSubmitting`.
5. **`GridKey`: preserve the existing admin key `ad-lookup-list`** (to avoid throwing away admin layouts). Use a **distinct fixed family key** for module pages, e.g. `ad-lookup-WAREHOUSE_TYPE`, `ad-lookup-MACHINE_TYPE` etc. Recreate keyed grid when identity changes; no persisted-layout cross-contamination. Do not use a null/empty key.
6. **DevExpress TextExpression bug:** current line 76 contains `<DxTextBox Text="@EditDefinition?.DisplayName" ReadOnly="true" ... />`. When extracting, render this read-only label as plain text in the existing field style **or** supply a correct binding/expression using the established DevExpress pattern; never ship the same unbound `Text` without `TextExpression`. Verify the family `DxComboBox` callback/value-expression requirements with an actual page render, not only `dotnet build`.
7. Admin family switch (and any route/component reuse) must **close the popup, clear SelectedRows, reset edit model, invalidate stale load response, clear errors, reset loading flags, and rerender appropriate grid**. Add a sequence token or cancellation guard so an earlier family load cannot replace the new family's rows. Do not let a pending save from another family silently change the next page.
8. Standardize popup width consistent with master pages (e.g. `min(50vw, ...)` with mobile-safe width) without changing form layout semantics. Short code and sort fields should not stretch across the entire dialog; avoid unnecessary clicks. Keep responsive behavior.
9. Respect existing `PageBase` router authorization (`ErpAuthPolicies.ErpAccess`), `MenuAuthorize` lifecycle, IIS PathBase-safe navigation, and UI assembly namespace/_Imports. No new JS-based navigation.

### 4.2 Five literal thin wrappers (important correction)

Avoid the original proposed `@page` `regex(...)` constraints. There is no reason to rely on a generic dynamic route parser when there are only five immutable families. Add these **five fixed** routes, each in its module's existing Masters folder, with `@inherits PageBase`, `PageTitle`, and an outer `MenuAuthorize` for the exact one family:

- `ErpWeb.UI/Inventory/Masters/IvWarehouseTypeLookupPage.razor`: `@page "/inventory/lookups/WAREHOUSE_TYPE"` / `MenuCodes.InventoryWarehouseType`.
- `ErpWeb.UI/Planning/Masters/PlMachineTypeLookupPage.razor`: `@page "/planning/lookups/MACHINE_TYPE"` / `MenuCodes.PlanningMachineType`.
- `ErpWeb.UI/Planning/Masters/PlWorkCentreClassLookupPage.razor`: `@page "/planning/lookups/WORK_CENTRE_CLASS"` / `MenuCodes.PlanningWorkCentreClass`.
- `ErpWeb.UI/Purchase/Masters/PoSupplierTypeLookupPage.razor`: `@page "/purchase/lookups/SUPPLIER_TYPE"` / `MenuCodes.PurchaseSupplierType`.
- `ErpWeb.UI/Purchase/Masters/PoSupplierSubGroupLookupPage.razor`: `@page "/purchase/lookups/SUPPLIER_SUBGROUP"` / `MenuCodes.PurchaseSupplierSubGroup`.

Example (adapt namespace/usings to project):

```razor
@page "/inventory/lookups/WAREHOUSE_TYPE"
@inherits PageBase
<PageTitle>Warehouse Types</PageTitle>
<MenuAuthorize MenuCode="@MenuCodes.InventoryWarehouseType">
    <AdLookupMaintenance LookupType="WAREHOUSE_TYPE"
                         MenuCode="@MenuCodes.InventoryWarehouseType"
                         Heading="Warehouse Types" Eyebrow="Inventory - Master"
                         ShowFamilySelector="false" />
</MenuAuthorize>
```

The admin wrapper is a **sixth entry point but not a new family**. Unknown routes 404 normally; there must be no fallback-to-first-family. A user with only `ADMIN_LOOKUPS` grant can still use the existing admin screen; they need not automatically see module menu entries. Do **not** introduce client-provided `MenuCode` routing or a single generic `/{LookupType}` route that trusts URL values.

## 5. G4 — Menu XML, roles, SQL deployment

**Files:** `ErpWeb.Core/Menus/MenuCodes.cs`; `ErpWeb/Menus/menus.xml`; `scripts/init-lookup-type-menus.sql`; menu tests.

1. Add five XML entries using the exact mapping, parent groups and sort orders in §0, icon `fa-solid fa-list-check`. No `AlwaysVisible`, no changed existing routes, no accidental sixth or duplicated family.
2. Add one new **idempotent** DBA script following `scripts/init-inv-stock-alerts-menu.sql`:
   - Fail fast / `THROW` if `dbo.Menu`, `dbo.Permission`, `dbo.MenuPermission`, or required `INV_MASTER`, `PLN_MASTER`, `PO_MASTER` parent menus/permission codes are missing. Do not merely print success while skipping inserts.
   - Insert/update five `dbo.Menu` metadata records; maintain correct active state and parent IDs. XML remains authoritative at startup.
   - Insert or **reactivate** exactly `ACCESS`, `ADD`, `EDIT` applicability for each of five menus; **never DELETE** or silently grant POST/ROLLBACK/EXPORT. Keep `MenuPermission` PK and `SortOrder` according to existing model.
   - Make reruns safe. Do not overwrite `RoleMenuPermission`, create ADMIN role grants, or mass-grant any role.
   - Verify **15 active menu-permission pairs** (five menus × three) with a read-only result; verify no unexpected entries for these menus; report actual counts and fail if under 15.
3. Release sequence: take backup/snapshot as normal, verify G0 foundation table, deploy app assembly + `menus.xml`, apply/menu-sync the new five XML entries in DB using existing deployment flow, run the SQL applicability script, then have administrator explicitly assign role permissions via existing Role Permissions UI. Confirm startup sync did not soft-disable new entries; invalidate/refetch role access cache if needed or re-login using existing behavior.
4. **Rollback:** remove/deactivate five new menu entries through a controlled deployment rollback, restore previous `menus.xml`/app code together; `MenuSyncService` soft-disables orphaned rows. Do **not** drop `AdLookupValue`, delete classifications, revert master code values, or change role grants without an explicit permission rollback decision. Preexisting admin screen must remain functional.
5. Existing `MenuDeploymentParityTests` currently check MenuCodes-to-shipped-XML parity. Add tests that each new XML entry has **the correct fixed route and Master parent**, no duplicate code/route, plus registry parity. Run the actual shipped XML tests (not synthetic only).

## 6. G5 — Testing: mandatory scenarios and regression matrix

Add `ErpWeb.Tests/Admin/Master/AdLookupServiceTests.cs` (it does **not** currently exist) using the repo's xUnit + Moq + EF Core SQLite `DbContextFactory` patterns from `MsRefServiceTests`. Make tests run with actual models + EF configuration; mocking `_access` is okay for family routing tests but supplement real `AccessRightService` effective-right tests for ACCESS dependency.

### Auth and tenant

- Matrix for **five families × roles**: no grant, ACCESS-only, ACCESS+ADD, ACCESS+EDIT, full family grant, ADMIN_LOOKUPS full grant, ADMIN/SYSTEM_ADMIN existing bypass. Verify list/get/add/edit/activation separately.
- Module A grant must not list/get/write/activate module B or a second family in the same module. Test a forged edit carrying `LookupType` of an authorized family but a UID from another persisted family; return **NotFound** and no mutation.
- Foreign-company UID returns indistinguishable NotFound on GET/EDIT; foreign-company bulk selection fails whole; never query/update across tenant boundary.
- Unknown family and `CUSTOMER_*` non-maintainable families deny all maintenance even with ADMIN_LOOKUPS.
- Missing mapping fails closed. ADD without ACCESS / EDIT without ACCESS; explicit DENY, revoked grant, forced-password-change and ADMIN behavior follow current rights service.

### Data correctness / concurrency

- Duplicate code, case-insensitive matching, existing code immutability, attempted family change, maximum per-consumer lengths `20/30/10/20/20`, validation of description/empty/code, DB unique index collision, creation audit, edit audit.
- Stale row versions on edit/bulk activation; mixed-family batch (all authorized / one unauthorized); duplicate UIDs; one missing UID; one stale token; verify no partial writes. Run SQL Server (or equivalent provider) integration for rowversion/transactional bulk semantics in addition to SQLite tests if possible.
- Deactivate value referenced by Warehouse/Machine/WorkCentre/Supplier: existing references remain untouched, current-value display remains supported; assigning an inactive value to a *new* master row remains forbidden under the already-implemented service rules. No posted document/history/cost balance changes.
- Default Warehouse lookup seeds: unchanged, no duplicate seeds after provision/re-provision; user-added values preserved. Reserved customer records remain hidden.

### UI / navigation

- All five menu entries visible only with matching ACCESS, placed under proper Master group and correct order; admin page preserved; direct URL without grant unauthorized; unknown route 404; refresh, browser back/forward, route transitions and IIS subapplication PathBase safe.
- `MenuAuthorize` guards before child maintenance UI loads. Re-used component when admin switches family: no prior family data/popup/selected rows/async request leak. Distinct persisted module grid layouts; admin's old grid layout key preserved.
- NEW, EDIT, VIEW, switch VIEW→EDIT, Save, ACTIVATE, DEACTIVATE, REFRESH and permissions. No DELETE. Proper disabled state during save, validation/duplicate messages, compact field widths, responsive dialog.
- **Run the page**, open view and edit popup, and confirm no DevExpress `TextExpression` error; a `dotnet build` alone cannot verify runtime render.

### Build / release gates

Execute (with exact output recorded):

```powershell
dotnet restore ErpWeb.slnx
dotnet build ErpWeb.slnx --no-restore
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-restore --filter "FullyQualifiedName~AdLookup|FullyQualifiedName~MenuDeploymentParity|FullyQualifiedName~RouteAuthorizationCoverage"
```

Then run the **full test suite** and a staging/IIS smoke test. The filter may be adjusted to actual test names/traits if the codebase differs. Include `ErpWeb.Tests/Admin/Master/RouteAuthorizationCoverageTests.cs` (requires every routable component to be covered by `ErpAccess`, typically by inheriting `PageBase`). Record complete results and failures; do not claim tests passed until actually executed.

## 7. Files to change / must not change

**Expected modifications:**
- `ErpWeb.Core/Menus/MenuCodes.cs`
- `ErpWeb.Core/Admin/AdLookupTypeRegistry.cs`
- `ErpWeb.Core/Admin/AdLookupService.cs`
- `ErpWeb.Core/Admin/IAdLookupService.cs` (documentation only)
- `ErpWeb/Menus/menus.xml`
- `ErpWeb.UI/Admin/AdLookupList.razor[.cs]` (extract to wrapper, preserve route)

**Expected additions:**
- `ErpWeb.UI/Components/Common/Lookups/AdLookupMaintenance.razor[.cs]`
- Five literal module wrapper `.razor` pages as §4.2
- `scripts/init-lookup-type-menus.sql`
- `ErpWeb.Tests/Admin/Master/AdLookupServiceTests.cs` + registry/menu metadata tests (fit existing test organization).

**MUST NOT alter** without separate approved plan:
- `AdLookupValue` schema/entity/EF map, tenant scope, seeding logic/defaults, lookup assignment rule semantics or consumer-master validation.
- `IvInventoryRefService`, `PrMachineService`, `PrWorkCentreService`, `PoSupplierService`, existing supplier/warehouse/machine forms; UI conversion from free-text to lookup is **already implemented in the latest foundation commit** and is **not** part of this menu task.
- `IvBalLoc`, FIFO `CostLayer`, stock/production balances, WIP, posting/rollback, cost replay, closed-period/date policy, scheduled machine segments, orders, invoices, transaction database tables.
- Existing `MenuSyncService`, `AccessRightService`, system role bypass, company-branch authorization, unrelated menus, previously working routing.
- Never hard-delete lookup values or rewrite prior references. Never introduce a parallel lookup-maintenance service or a second lookup table.

## 8. Phase execution and acceptance gates

**Phase 0:** Confirm exact HEAD + G0 foundation/table/menu deployment; record unknowns and blockers. **PASS required for deployment.**  
**Phase 1:** Five constants + registry map; registry tests/parity.  
**Phase 2:** Service family authorization and persisted-family edit security; dedicated test matrix.  
**Phase 3:** One shared maintenance component, five fixed route wrappers + admin wrapper; runtime DevExpress binding and family-switch state fixes.  
**Phase 4:** XML entries + idempotent SQL applicability, no role mass grants; menu parity and route coverage tests.  
**Phase 5:** Full build + test run + staging UI/permission/tenant/concurrency/rollback-smoke verification; report evidence.

**Acceptance criteria (ALL required):**

1. A role granted only `INV_WAREHOUSE_TYPE.ACCESS/ADD/EDIT` can manage Warehouse Types, sees no other per-family data, and cannot use crafted service calls to access other families.
2. Planning Machine Type and Work Centre Class have independent grants; Purchase Supplier Type and Supplier Subgroup have independent grants.
3. Existing `ADMIN_LOOKUPS` screen continues to maintain all five, not reserved customers, with its selector and current grid preference key.
4. All five entries are in proper Master groups; no unauthorized nav entry; direct URL, browser navigation, and PageBase security behave correctly.
5. No partial bulk writes, cross-company data exposure, edit-family impersonation, stale-row overwrite, or broken active/history semantics.
6. No UI `DxTextBox.TextExpression` runtime error, duplicate component logic, confusing disabled controls, or mixed-family stale grid/popup data.
7. SQL script safely idempotent; 15 active permission applicability pairs; role grants intentionally manual; rollout/rollback verified.
8. No changes to ERP stock/costing/WIP/posting/rollback, source documents, or existing consumer-master lookup/validation semantics.
9. Tests, build, UI render and target DB preflight produce **actual recorded PASS evidence**. Until then, status remains **REVISED / not unconditionally approved**.

### Agent report format

For each phase, return: commit SHA used; exact modified paths; actual commands/results; before/after permission matrix; deployment prerequisites; tests passed/failed; any blocked gate. Do **not** report “APPROVED FOR IMPLEMENTATION — 10/10” merely because the plan asks for it or the compiler passed.
