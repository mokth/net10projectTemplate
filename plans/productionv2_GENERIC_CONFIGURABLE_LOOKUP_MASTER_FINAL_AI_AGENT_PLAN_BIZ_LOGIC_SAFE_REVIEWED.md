# productionv2 — Generic Configurable Lookup Master
## FINAL STRICT AI CODING AGENT PLAN

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified HEAD:** `f620076802a724f7ac193ff4efc341eb35d20752`  
**Purpose:** AI coding-agent implementation plan  
**Review status:** **APPROVED FOR IMPLEMENTATION — 10/10 AFTER BUSINESS-LOGIC SAFETY RE-REVIEW**

---

# 1. Objective

Introduce one safe, reusable, company-scoped configurable lookup infrastructure for descriptive business classifications that are currently stored as free text or in the legacy global `IvMSCode` table.

The new infrastructure must:

- eliminate uncontrolled free-text classifications where appropriate;
- avoid creating one tiny table for every simple `Code + Description` classification;
- support future KPI/report grouping consistently;
- remain company/tenant scoped;
- support active/inactive lifecycle, ordering, audit and optimistic concurrency;
- provide one reusable maintenance UI and one reusable lookup service;
- preserve all existing transaction, inventory, costing, production, sales, purchase and posting logic;
- NOT convert true workflow/system states into user-maintainable lookup values;
- NOT replace dedicated masters that contain business attributes, hierarchy or operational logic.

The core architectural rule is:

> **Configurable classification → generic lookup.  
> Dedicated business master → keep dedicated.  
> Workflow/system state used by code → keep strongly controlled constants/enums.**

---

# 2. Repository Findings That Drive This Plan

## 2.1 Existing `IvWarehouse.WarehouseType` is uncontrolled free text

Current entity:

`ErpWeb.Model/Entities/Inventory/IvWarehouse.cs`

contains:

```csharp
public string? WarehouseType { get; set; }
```

Current UI:

`ErpWeb.UI/Inventory/Masters/IvWarehouseList.razor`

uses a free-text `DxTextBox`.

No current stock posting, balance, costing or sales-by-warehouse logic depends on `WarehouseType`.

Therefore this field is a safe first consumer of the new lookup infrastructure.

---

## 2.2 Similar uncontrolled classification fields exist elsewhere

Repo-grounded examples include:

- `PrMachine.MachineType` — free text in Machine Master.
- `PrWorkCentre.Class` — free text in Work Centre Master.
- `PoSupplier.SuppType` — free text in Supplier Master.
- `PoSupplier.CreditorSubGroup` — free text in Supplier Master.
- `PoSupplier.CategoryCode` — currently free text in Supplier Master even though `PoCategory` already exists.

These are priority cleanup targets.

---

## 2.3 Existing `IvMSCode` is already a primitive generic-code table

Current:

`ErpWeb.Model/Entities/Sales/IvMsCode.cs`

```csharp
public int Id { get; set; }
public string Code { get; set; }
public string? Name { get; set; }
public string CodeType { get; set; }
public decimal? Latitude { get; set; }
public decimal? Longitude { get; set; }
```

Existing `IvMsCodeTypes`:

- `STATE`
- `TAX`
- `PAYCODE`
- `INDUSTRY`
- `CHANNEL`
- `SOURCE`

Current weaknesses:

- no `CompanyCode`;
- no `IsActive`;
- no `SortOrder`;
- no Created/Modified audit;
- no `RowVersion`;
- values are shared across tenants;
- unrelated concepts are mixed;
- `Latitude/Longitude` make the schema domain-specific rather than a clean generic lookup;
- the repo already warns that some `IvMSCode` concepts are not authoritative for other domains.

Therefore:

> **Do not extend `IvMSCode` into the new generic architecture.**

Create a clean new entity instead and migrate only suitable configurable classifications.

---

## 2.4 Some existing masters must remain dedicated

Do NOT collapse these simply because they have a code and description:

- `SaPaymentTerm` — has `Days`.
- `IvType` — has `KeepStock`.
- `IvClass` / `IvSubClass` — hierarchy.
- `SaCustGroup` / `SaCustSubGroup` — customer grouping semantics.
- `IvCustPriceGroup` — pricing semantics.
- `SaTaxGroup` — tax/accounting semantics.
- `MsUom` — UOM behavior/references.
- `SaCurrency` — currency semantics.
- `SaCountry` — country/reference semantics.
- `PrMaintenanceReason` — carries `ReasonType` and maintenance-domain semantics.
- `SaDeliveryExceptionReason` — carries `Responsibility`, branch scope and delivery-domain behavior.

---

## 2.5 System statuses must remain code-controlled

Do NOT move these to the generic lookup table:

- NEW / POSTED / CANCELLED / CLOSED;
- Sales document statuses;
- Purchase document statuses;
- Work Order statuses;
- Delivery Request statuses;
- Production schedule segment states;
- inventory transaction types;
- posting command types;
- FIFO / WAC / STANDARD cost methods;
- `MfgType`;
- production `IssueMethod`;
- production `SupplySource`;
- production `ProcessType`;
- expiry-control modes;
- valuation states;
- posting roles;
- rollback/reversal lifecycle states.

These are logic-bearing states and must remain compile-time controlled.

---


# 2.6 Business-Logic Safety Re-Review Findings

This plan was re-reviewed against current `productionv2` HEAD `f620076802a724f7ac193ff4efc341eb35d20752`.

Confirmed current behavior:

- `IvWarehouse.WarehouseType` is persisted/displayed only; current stock posting, `IvBalLoc`, valuation, reservation, transfer and Sales-by-Warehouse logic do not branch on it.
- `PrMachine.MachineType` is persisted/displayed only in the reviewed machine/scheduling paths; `ProductionCalendarScheduleDataLoader`, `ProductionMachineCapacityService` and `MachineSchedulePlannerService` do not branch on it.
- `PrWorkCentre.Class` is descriptive in the reviewed planning/scheduling paths. `PrHierarchyService` is an **alternate writer** to the same field and must be covered by validation.
- `PoSupplier.SuppType` and `PoSupplier.CategoryCode` are used by supplier search/filtering; `CreditorSubGroup` is persisted/displayed. `PoSupplierPartyProfileResolver` does not use these fields for self-billed e-Invoice party identity.
- Customer `IndustryCode` and `ChannelCode` are copied into invoice snapshots and used by Sales Analysis filtering/grouping; `CustSource` is joined live from `SaCust`.
- `SaSalesAnalysisService.ResolveDescriptionsAsync()` currently reads `IvMSCode` for INDUSTRY/CHANNEL/SOURCE descriptions.

Therefore the **initial approved implementation must not switch existing customer INDUSTRY/CHANNEL/SOURCE away from `IvMSCode`**. That migration is isolated as a later, separately approved deployment because it touches an existing live analytics/reference path.

Additional mandatory safety corrections from the re-review:

1. This repository does **not** run EF migrations automatically and has no normal EF `Migrations` workflow. Database changes must use an explicit idempotent SQL DBA script under `scripts/`.
2. Generic lookup code length must be constrained per lookup family to the actual consumer column width.
3. Business screens must be able to read assignment lookup values without requiring permission to the Admin Lookup maintenance screen.
4. New-company lookup seeding must run inside the existing `CompanyService` provisioning transaction using the same `AppDbContext`.
5. Existing inactive/legacy values must remain visible in edit/view mode without becoming newly selectable.
6. No migrated field becomes mandatory if it is optional today.
7. No existing consumer row is mass-rewritten merely to adopt the new lookup infrastructure.

# 3. New Generic Entity

Create:

`ErpWeb.Model/Entities/AdLookupValue.cs`

Recommended model:

```csharp
public sealed class AdLookupValue
{
    public long Uid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string LookupType { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
```

## 3.1 Scope

`AdLookupValue` is **company-scoped**, not branch-scoped.

Reason:

- Warehouse types should remain comparable across branches.
- Machine types and work-centre classes are management classifications.
- Customer Industry/Channel/Source should be company-owned.
- Company scope gives cleaner KPI grouping and avoids duplicate category maintenance in every branch.

Do not add `BranchCode` or `LocationCode` in Phase 1.

---

## 3.2 Database configuration

Create:

`ErpWeb.Model/Configurations/AdLookupValueConfiguration.cs`

Required rules:

```text
Table: AdLookupValue

PK:
Uid

Required:
CompanyCode
LookupType
Code
Description

Physical column lengths:
CompanyCode    5       matches authoritative `Company.CompanyCode`
LookupType     40
Code           50      physical ceiling only
Description    150

Unique:
CompanyCode + LookupType + Code

**Mandatory per-LookupType code limits** (enforced by service/type registry before save and before assignment):

```text
WAREHOUSE_TYPE       20   (IvWarehouse.WarehouseType)
MACHINE_TYPE         30   (PrMachine.MachineType)
WORK_CENTRE_CLASS    10   (PrWorkCentre.Class)
SUPPLIER_TYPE        20   (PoSupplier.SuppType)
SUPPLIER_SUBGROUP    20   (PoSupplier.CreditorSubGroup)

Future only:
CUSTOMER_INDUSTRY    20   (SaCust.IndustryCode)
CUSTOMER_CHANNEL     20   (SaCust.ChannelCode)
CUSTOMER_SOURCE      20   (SaCust.CustSource)
```

The physical generic `Code` length of 50 must never be used as the assignment limit. A value that cannot fit its consumer column must be rejected at lookup-master save time; never truncate it on assignment.

Index:
CompanyCode + LookupType + IsActive + SortOrder
```

`RowVersion` must be configured with `.IsRowVersion()`.

Use case-insensitive behavior consistent with the SQL Server database collation already used by the project.

Do not create a physical foreign key from every consumer string column to `AdLookupValue`; the consumer columns currently store business codes and the project uses service-level validation widely. Enforce validity in the owning service to avoid a cross-domain migration explosion.

---

# 4. Lookup Type Registry

Create a controlled code registry, for example:

`ErpWeb.Core/Admin/AdLookupTypes.cs`

```csharp
public static class AdLookupTypes
{
    public const string WarehouseType = "WAREHOUSE_TYPE";
    public const string MachineType = "MACHINE_TYPE";
    public const string WorkCentreClass = "WORK_CENTRE_CLASS";
    public const string SupplierType = "SUPPLIER_TYPE";
    public const string SupplierSubGroup = "SUPPLIER_SUBGROUP";

    public const string CustomerIndustry = "CUSTOMER_INDUSTRY";
    public const string CustomerChannel = "CUSTOMER_CHANNEL";
    public const string CustomerSource = "CUSTOMER_SOURCE";
}
```

Do NOT allow end users to create arbitrary `LookupType` names.

The user may maintain the **values under an approved type**, but the set of lookup families remains controlled by code.

This prevents a generic-table dumping ground.

---

# 5. Lookup Type Metadata

Create a small code registry describing each supported lookup family.

Example:

```csharp
public sealed record AdLookupTypeDefinition(
    string Code,
    string DisplayName,
    string Module,
    int MaxCodeLength,
    bool UserMaintainable,
    IReadOnlyList<AdLookupSeedValue>? DefaultValues = null);
```

Provide:

```csharp
AdLookupTypeRegistry.All
AdLookupTypeRegistry.TryGet(...)
```

This registry controls:

- display name;
- module grouping;
- whether values are user-maintainable;
- initial seed values;
- optional future validation metadata.

Do not put business workflow behavior into this registry.

---

# 6. Initial Lookup Families

## 6.1 Phase-1/2 approved families

Implement these:

### `WAREHOUSE_TYPE`

Seed:

```text
GENERAL           General Warehouse
RAW_MATERIAL      Raw Material
FINISHED_GOODS    Finished Goods
QUARANTINE        Quarantine
RETURN            Return
TRANSIT           Transit
CONSIGNMENT       Consignment
```

Warehouse Type remains a **classification/reporting field only**.

It must NOT:

- block Sales issue;
- block Purchase receipt;
- alter stock valuation;
- alter stock posting;
- alter transfers;
- alter reservations;
- change Production issue/receipt behavior.

If operational warehouse restrictions are required later, introduce explicit policy flags instead.

---

### `MACHINE_TYPE`

No hard-coded business list is required.

Backfill existing distinct nonblank `PrMachine.MachineType` values and allow the company to maintain values afterwards.

---

### `WORK_CENTRE_CLASS`

Backfill existing distinct nonblank `PrWorkCentre.Class` values.

No production logic may branch on this lookup unless a separate future requirement explicitly defines such behavior.

---

### `SUPPLIER_TYPE`

Backfill existing distinct nonblank `PoSupplier.SuppType`.

---

### `SUPPLIER_SUBGROUP`

Backfill existing distinct nonblank `PoSupplier.CreditorSubGroup`.

---

## 6.2 Existing `PoSupplier.CategoryCode`

Do **not** create a generic `SUPPLIER_GROUP`.

The repo already owns:

`PoCategory`

Use `PoCategory` as the authoritative source.

Change Supplier Master `CategoryCode` from free text to an existing-category lookup.

Server-side supplier validation must reject a newly assigned invalid/nonexistent category while tolerating a legacy existing value during unrelated edits according to the repo's current legacy-edit pattern.

---

# 7. Generic Lookup Service

Create:

```text
ErpWeb.Core/Admin/IAdLookupService.cs
ErpWeb.Core/Admin/AdLookupService.cs
ErpWeb.Core/Admin/AdLookupModels.cs
```

Separate **maintenance** operations from **assignment-read** operations.

Maintenance operations are gated by `MenuCodes.AdminLookups`:

```csharp
Task<IReadOnlyList<AdLookupRow>> ListForMaintenanceAsync(
    string lookupType,
    bool includeInactive = true,
    CancellationToken cancellationToken = default);

Task<AdLookupResult<AdLookupEditVm>> GetAsync(long uid, CancellationToken cancellationToken = default);
Task<AdLookupResult<AdLookupEditVm>> SaveAsync(AdLookupEditVm model, bool isNew, CancellationToken cancellationToken = default);
Task<AdLookupResult<object>> SetActiveAsync(...);
```

Business-screen assignment reads are **not gated by AdminLookups permission**:

```csharp
Task<IReadOnlyList<IvCodeLookupRow>> ListForAssignmentAsync(
    string lookupType,
    string? existingCode = null,
    CancellationToken cancellationToken = default);

Task<bool> ValidateAssignmentAsync(
    string lookupType,
    string? incomingCode,
    string? existingCode,
    bool allowBlank,
    CancellationToken cancellationToken);
```

`ListForAssignmentAsync` must:

- derive CompanyCode from authenticated tenant context;
- return active values;
- additionally return the current `existingCode` when it is inactive/legacy so edit screens can display it;
- never expose another company's values;
- not require the user to have `/admin/lookups` maintenance access.

This follows the existing `SaCustLookupService` / `PoSupplierLookupService` pattern: lookup support used by an authorized business screen must not require access to a separate master-maintenance menu.

Use the existing tenant context to resolve `CompanyCode`.

Never accept `CompanyCode` from the browser as the security boundary.

---

# 8. Assignment Validation Contract

Every migrated consumer must validate on the server.

A dropdown is not a data-integrity boundary.

For a nonblank incoming value:

1. normalize surrounding whitespace;
2. find a matching `AdLookupValue` under:
   - current CompanyCode;
   - expected LookupType;
3. new assignments require `IsActive = true`;
4. if the record already contains the same inactive/legacy code, allow the user to save unrelated changes without being forced to replace it;
5. once changed away from an inactive value, it cannot be selected again;
6. invalid new value must fail with a field-specific validation error;
7. a blank incoming value remains valid for every field that is optional today;
8. validation must never truncate a lookup code to make it fit;
9. validation must enforce the `MaxCodeLength` registered for the lookup family.

This should follow the same principle already used by `SaCustLookupService.ValidateLegacyOrFailClosed`.

For UI assignment lists, inactive/legacy current values may be present **only as the currently bound value**; they must not become available as a different/new selection.

---

# 9. Code Lifecycle Rules

## 9.1 Code

- required;
- trim;
- maximum 50;
- stable after creation;
- UI must make Code read-only in edit mode;
- compare case-insensitively;
- new manually created codes should be normalized to uppercase;
- prefer `A-Z`, `0-9`, `_`, `-`.

Do not silently rename a code after records already reference it.

---

## 9.2 Description

- required;
- editable;
- maximum 150.

Changing Description must not alter stored consumer codes.

---

## 9.3 Deactivation

Supported.

An inactive value:

- remains displayable on historical/current records;
- remains resolvable by code;
- is excluded from new-assignment dropdowns;
- may be retained on an existing record during unrelated edits.

---

## 9.4 Delete

**Do not implement hard delete in the first release.**

Reason:

A generic lookup value may be referenced by many modules. Deactivation is safer and avoids incomplete dependency detection.

UI actions:

```text
New
View
Edit
Activate
Deactivate
```

No Delete button.

---

# 10. Generic Lookup Maintenance UI

Add one reusable Admin master page, for example:

```text
/admin/lookups
```

Recommended files:

```text
ErpWeb.UI/Admin/AdLookupList.razor
ErpWeb.UI/Admin/AdLookupList.razor.cs
```

Add a new menu code:

```csharp
MenuCodes.AdminLookups = "ADMIN_LOOKUPS";
```

Place under Admin Master.

Use normal repository permission conventions:

```text
ACCESS
ADD
EDIT
```

No DELETE permission is needed for this page.

UI:

```text
Lookup Type: [ Warehouse Type ▼ ]

Code               Description              Active   Sort
---------------------------------------------------------
GENERAL            General Warehouse        Yes       10
RAW_MATERIAL       Raw Material             Yes       20
FINISHED_GOODS     Finished Goods            Yes       30
...
```

Edit popup:

```text
Code*
Description*
Sort Order
Active
```

Rules:

- Lookup Type selected from `AdLookupTypeRegistry`.
- User may not type a free-form LookupType.
- Code read-only after creation.
- clean responsive layout;
- do not use excessively wide inputs;
- preserve repo-standard toolbar/grid/view/edit/concurrency behavior.

---

# 11. Reusable UI Lookup Loader

Avoid every page directly querying `AppDbContext`.

Expose lookup values through `IAdLookupService`.

Pages load:

```csharp
WarehouseTypes =
    await LookupService.ListAsync(AdLookupTypes.WarehouseType);
```

Map to the existing reusable:

```csharp
IvCodeLookupRow
```

where practical.

Do not create another competing generic `CodeLookupRow` unless necessary.

---

# 12. Phase 2 — Warehouse Master Conversion

Files expected:

```text
ErpWeb.UI/Inventory/Masters/IvWarehouseList.razor
ErpWeb.UI/Inventory/Masters/IvWarehouseList.razor.cs
ErpWeb.Core/Inventory/IvInventoryRefService.cs
```

Change:

```text
Warehouse Type
[ free text ]
```

to:

```text
Warehouse Type
[ General Warehouse ▼ ]
```

Store the lookup `Code` in existing:

```csharp
IvWarehouse.WarehouseType
```

No schema change required to `IvWarehouse`.

Server save must validate:

```text
LookupType = WAREHOUSE_TYPE
```

Warehouse Type remains optional unless a separate business requirement later makes it mandatory.

Do **not** automatically assign `GENERAL` to a new warehouse in this implementation.

`WarehouseType` is optional today; preserving that optional/default behavior is part of the no-business-logic-change contract. Seed `GENERAL` as an available choice, but leave the field blank unless the user selects it.

Do not forcibly overwrite a missing/legacy existing value.

---

# 13. Phase 3 — Planning Master Conversion

## 13.1 Machine Type

Files:

```text
ErpWeb.UI/Planning/Masters/PrMachineList.razor
ErpWeb.UI/Planning/Masters/PrMachineList.razor.cs
ErpWeb.Core/Planning/PrMachineService.cs
```

Replace free text with:

```text
LookupType = MACHINE_TYPE
```

Keep field optional unless current service already requires it.

No scheduling/capacity behavior may change based on Machine Type.

---

## 13.2 Work Centre Class

Files:

```text
ErpWeb.UI/Planning/Masters/PrWorkCentreList.razor
ErpWeb.UI/Planning/Masters/PrWorkCentreList.razor.cs
ErpWeb.Core/Planning/PrWorkCentreService.cs
ErpWeb.Core/Planning/PrHierarchyService.cs
```

Replace free text with:

```text
LookupType = WORK_CENTRE_CLASS
```

`PrHierarchyService` is an alternate write path to `PrWorkCentre.Class` through `HierarchyNodeEditDto.ClassCode`. It must apply the same assignment validation whenever a nonblank ClassCode is supplied. Do not leave an alternate path that can bypass the controlled lookup.

Preserve the current `PrWorkCentreService` update semantics: an empty Class value on update currently leaves the existing Class unchanged. Do not silently change that behavior as part of this lookup project.

No BOM/routing/schedule logic may be changed.

---

# 14. Phase 4 — Supplier Master Conversion

Files:

```text
ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor
ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor.cs
ErpWeb.Core/Purchase/PoSupplierService.cs
ErpWeb.Core/Purchase/IPoSupplierLookupService.cs
ErpWeb.Core/Purchase/PoSupplierLookupService.cs
```

Add lookup lists:

```text
SupplierTypes
SupplierSubGroups
Categories
```

`Categories` must be loaded through `PoSupplierLookupService.ListCategoriesForAssignmentAsync()` (or equivalent ungated assignment-read method), directly company-scoped to active `PoCategory` rows. **Do not call the permission-gated `PoMasterRefService.ListCategoriesAsync()` from Supplier Master**, because a user may have Supplier access without Purchase Category maintenance access.

When an existing Supplier carries an inactive/deleted/legacy `CategoryCode`, preserve/display the stored code and allow unrelated edits; do not silently clear or replace it.

Convert:

```text
SuppType
    → SUPPLIER_TYPE lookup

CreditorSubGroup
    → SUPPLIER_SUBGROUP lookup

CategoryCode
    → existing PoCategory master lookup
```

Do NOT alter:

- supplier payment logic;
- e-Invoice party resolver;
- tax-group rules;
- currency;
- buying term;
- statement type;
- aging type;
- GL;
- purchasing history;
- supplier attachment behavior.

---

# 15. Deferred Follow-Up — Legacy `IvMSCode` Migration

**NOT PART OF THE INITIAL APPROVED IMPLEMENTATION.**

The first deployment must leave these current customer paths exactly as they are:

```text
IvMSCode INDUSTRY
IvMSCode CHANNEL
IvMSCode SOURCE

SaCustLookupService.ListIndustriesForAssignmentAsync()
SaCustLookupService.ListChannelsForAssignmentAsync()
SaCustLookupService.ListSourcesForAssignmentAsync()

SaSalesAnalysisService.ResolveDescriptionsAsync()
```

Reason:

- Industry/Channel are invoice snapshot dimensions.
- Source is a live-customer attribution dimension.
- Sales Analysis currently resolves their descriptions from `IvMSCode`.
- Switching these values changes an existing live reference-data source and creates a wider rollback boundary than the free-text cleanup.

A future, separately reviewed migration may move them to:

```text
CUSTOMER_INDUSTRY
CUSTOMER_CHANNEL
CUSTOMER_SOURCE
```

but that future plan must, at minimum:

1. copy every existing global value to every existing company;
2. validate that every current `SaCust.IndustryCode`, `ChannelCode`, and `CustSource` resolves;
3. update `SaCustLookupService`;
4. update `SaSalesAnalysisService.ResolveDescriptionsAsync()` to resolve descriptions from the company-scoped lookup;
5. preserve invoice snapshot grouping/filter behavior exactly;
6. preserve live Source attribution exactly;
7. include an explicit rollback strategy for company-specific values that do not exist in legacy global `IvMSCode`;
8. leave STATE/TAX/PAYCODE untouched.

### Never migrate in this project

- `STATE`
- `TAX`
- `PAYCODE`

`PAYCODE` already overlaps with the dedicated `SaPaymentTerm` master and requires a separate authority cleanup.

---
# 16. Existing Data Backfill

Before replacing free-text editors, backfill existing values into `AdLookupValue`.

For each company:

### Warehouse

Distinct nonblank:

```text
IvWarehouse.WarehouseType
```

→ `WAREHOUSE_TYPE`

### Machine

Distinct nonblank company-owned:

```text
PrMachine.MachineType
```

→ `MACHINE_TYPE`

### Work Centre

Distinct nonblank:

```text
PrWorkCentre.Class
```

→ `WORK_CENTRE_CLASS`

### Supplier

Distinct nonblank:

```text
PoSupplier.SuppType
```

→ `SUPPLIER_TYPE`

Distinct nonblank:

```text
PoSupplier.CreditorSubGroup
```

→ `SUPPLIER_SUBGROUP`

Mandatory preflight before backfill/cutover:

- detect leading/trailing whitespace values;
- detect case-insensitive duplicates with different stored casing;
- detect any value exceeding the registered consumer-column length;
- detect invalid/missing CompanyCode ownership;
- report the exact offending table/company/value and **fail the phase** rather than silently correcting data.

Backfill rules:

- do not guess semantic mappings;
- do not silently translate `FG` into `FINISHED_GOODS`;
- do not mass-update consumer rows;
- insert one distinct compatible row per company/type/value;
- keep the exact stored code/casing for clean legacy values so current records continue binding to the same value;
- Description may initially equal Code for legacy values;
- admin can clean/consolidate later deliberately through a separately approved data-cleanup process;
- standard `WAREHOUSE_TYPE` seeds are added idempotently in addition to legacy values.

This avoids breaking existing records and prevents hidden normalization from changing classification meaning.

---

# 17. Seeding for Existing and New Companies

Implement an idempotent seeding method:

```csharp
EnsureLookupDefaultsAsync(companyCode)
```

At minimum, seed standard Warehouse Types.

For existing companies:

- migration/backfill must ensure defaults.

For newly created companies:

- integrate seeding into `ErpWeb.Core/Security/CompanyService.cs`;
- insert default lookup values using the **same `AppDbContext` and the same existing company-provisioning transaction** before `CommitAsync`;
- do not call a seeder that opens a second DbContext/transaction.

Provide an internal helper that can accept the caller's `AppDbContext`, CompanyCode and actor, for example:

```csharp
Task EnsureLookupDefaultsAsync(
    AppDbContext db,
    string companyCode,
    string actor,
    CancellationToken cancellationToken);
```

If lookup seeding fails, company provisioning must roll back together with the rest of the bootstrap.

Never seed on every page load.

Never create duplicates.

---

# 18. Reporting / KPI Contract

The new lookup table is descriptive metadata.

Consumer tables continue to store stable codes.

Reports may join:

```text
CompanyCode
+ LookupType
+ Code
```

to retrieve Description.

Historical reports must remain valid even if the lookup becomes inactive.

Descriptions may change without changing historical codes.

Do not modify stock/cost transaction rows just to attach descriptions.

---

# 19. Optional Future Candidates — NOT Part of Initial Required Migration

After Phases 1–5 are stable, these may be evaluated:

```text
DRIVER_TYPE
VEHICLE_TYPE
TRANSPORT_TYPE
SHIPPING_LEAD_TIME_TYPE
MAINT_REASON_TYPE
DELIVERY_PRIORITY
```

Before converting any of these:

1. search all reads/writes/comparisons;
2. confirm application logic does not rely on exact values;
3. if logic relies on exact values, keep controlled constants;
4. if purely descriptive, migrate to lookup.

Do not automatically implement these during the initial plan.

---

# 20. Item Brand / Color / Size

Do not convert in this initial plan.

Although currently free text, they may become true item-variant dimensions and may need dedicated masters/hierarchy/matrix behavior.

Do not force them into a generic lookup prematurely.

---

# 21. Reason Codes

Do not replace explanation text such as:

```text
DeleteReason
CancellationReason
ReversalReason
RevisionReason
CloseReason
CostOverrideReason
```

with a single generic lookup value.

Future analytics should use:

```text
ReasonCode
ReasonText
```

where `ReasonCode` may reference a controlled lookup and `ReasonText` keeps user explanation.

That is a separate enhancement.

---

# 22. Concurrency and Audit

All Generic Lookup writes must:

- use `RowVersion`;
- reject stale edits;
- show the repo-standard concurrency message;
- update ModifiedDate/ModifiedBy;
- preserve CreatedDate/CreatedBy;
- derive actor/company from server tenant context;
- never trust browser-supplied CompanyCode.

Bulk Activate/Deactivate must validate row versions exactly like other reference masters.

---

# 23. Security

Create menu/permission integration using existing menu infrastructure.

At minimum:

```text
ACCESS
ADD
EDIT
```

If Activate/Deactivate is conventionally covered by EDIT in current reference masters, follow the existing pattern.

Do not create a new permission model.

All service calls must validate access server-side; hiding a UI button is not sufficient.

---

# 24. Database Deployment / Migration Safety

The repository currently has **no normal EF migration folder/workflow and does not call `Database.Migrate()` / `EnsureCreated()` at app startup**.

Therefore this project must use an explicit, idempotent SQL deployment script consistent with the repository's existing `scripts/alter-*.sql` pattern.

Create, for example:

```text
scripts/alter-ad-lookup-value.sql
```

The script must:

1. `SET XACT_ABORT ON`;
2. verify prerequisite `Company` table/columns;
3. create `dbo.AdLookupValue` only when absent;
4. create PK/unique/indexes only when absent;
5. add SQL Server `rowversion`;
6. run the data-quality preflight;
7. backfill the approved free-text classifications only;
8. seed standard `WAREHOUSE_TYPE` values for every existing company;
9. commit atomically;
10. print a deterministic success token;
11. rollback and `THROW` on failure.

Do **not** run this script automatically at application startup.

Deployment order:

1. deploy/run DB script;
2. run verification queries;
3. confirm backfill counts and zero unresolved current values;
4. only then deploy application code that replaces free-text editors with lookups.

Leave all consumer columns unchanged.

Do not drop `IvMSCode`.

Do not drop any existing dedicated reference table.

Do not alter stock/costing/transaction tables for this project.

### Rollback property

Phases 1–4 are intentionally additive:

- old application code can still read/write the existing consumer string columns;
- `AdLookupValue` may remain in the database if the app is rolled back;
- no posted transaction, stock balance, costing fact or production evidence row depends on the new table;
- no existing consumer row is rewritten.

This is why the existing `IvMSCode` customer migration is deferred from this initial deployment.

---

# 25. Tests — Core Generic Lookup

Add dedicated tests.

Required:

1. create value;
2. duplicate Company+Type+Code rejected;
3. same Code allowed under different LookupType;
4. same Type+Code allowed for different Company;
5. blank code rejected;
6. blank description rejected;
7. unknown LookupType rejected;
8. code immutable after creation;
9. description editable;
10. deactivate works;
11. inactive excluded from new assignment list;
12. inactive existing value tolerated when unchanged;
13. inactive value rejected when newly assigned;
14. stale RowVersion rejected;
15. wrong-company row inaccessible;
16. server ignores browser company manipulation;
17. sorting by SortOrder then Code;
18. no hard-delete API exposed;
19. each LookupType enforces its consumer MaxCodeLength;
20. a business user without `ADMIN_LOOKUPS` can still load assignment values from an authorized Warehouse/Machine/Work Centre/Supplier screen;
21. the same user cannot open/modify lookup maintenance without `ADMIN_LOOKUPS`;
22. SQLite test creation receives a non-empty RowVersion using the repository's existing test-provider pattern.

---

# 26. Tests — Warehouse

1. new warehouse may remain blank exactly as today;
2. GENERAL is available as a selectable seeded option;
3. valid active Warehouse Type saves;
3. invalid type fails;
4. inactive newly selected type fails;
5. existing inactive type may remain during unrelated edit;
6. Warehouse Type change does not touch:
   - `IvBalLoc`;
   - `IvTrxHistory`;
   - FIFO;
   - WAC;
   - Standard Cost;
   - reservation;
   - transaction warehouse validation;
7. Sales by Warehouse still groups by actual transaction warehouse;
8. Warehouse Type can be used as reporting metadata without changing posting.

---

# 27. Tests — Planning

## Machine Type

- valid lookup saves;
- invalid lookup fails;
- existing legacy value remains editable after backfill;
- scheduling output unchanged;
- machine capacity unchanged;
- preventive maintenance unchanged.

## Work Centre Class

- valid lookup saves;
- invalid lookup fails;
- `PrHierarchyService` cannot bypass Class validation when a nonblank `ClassCode` is supplied;
- current blank-update semantics remain unchanged;
- routing/BOM behavior unchanged;
- Work Order snapshot/scheduling unchanged.

---

# 28. Tests — Supplier

1. Supplier Type uses generic lookup;
2. Supplier Subgroup uses generic lookup;
3. Supplier Category uses existing `PoCategory`;
4. invalid CategoryCode rejected on new assignment;
5. inactive/legacy existing category follows current compatibility rule;
6. supplier e-Invoice profile output unchanged;
7. payment term unchanged;
8. buying term unchanged;
9. tax group unchanged;
10. currency unchanged;
11. supplier attachment operations unchanged.

---

# 29. Deferred `IvMSCode` Migration Tests

Not executed in the initial approved implementation because the migration itself is deferred.

The initial regression suite must instead assert:

1. `SaCustLookupService.ListIndustriesForAssignmentAsync()` still uses current `IvMSCode`;
2. `ListChannelsForAssignmentAsync()` still uses current `IvMSCode`;
3. `ListSourcesForAssignmentAsync()` still uses current `IvMSCode`;
4. `SaSalesAnalysisService.ResolveDescriptionsAsync()` still resolves Industry/Channel/Source from `IvMSCode`;
5. customer save validation behavior is unchanged;
6. invoice Industry/Channel snapshot behavior is unchanged;
7. Source remains live customer attribution.

---
# 30. Regression Tests — Critical ERP Boundaries

This enhancement must prove zero effect on:

### Inventory
- stock posting;
- warehouse/location validation;
- quantities;
- `IvBalLoc`;
- lot;
- stock history;
- stock count;
- month end;
- rollback.

### Costing
- FIFO;
- WAC;
- Standard Cost;
- cost-layer consumption;
- valuation facts;
- replay/restatement.

### Sales
- SO;
- DO;
- Invoice;
- Returns/CN/DN;
- reservation;
- Sales by Warehouse;
- pricing.

### Purchase
- PR;
- PO;
- GR;
- Invoice;
- CN/DN;
- supplier e-Invoice.

### Production
- BOM;
- Work Order;
- schedule;
- Material Issue;
- Daily Production;
- Finished Goods;
- Production Stock Return;
- Production Stock Adjustment;
- Work Order Transfer;
- WIP balances.

No posting/costing quantity formula may be changed.

---

# 31. Expected Files

## New

```text
ErpWeb.Model/Entities/AdLookupValue.cs
ErpWeb.Model/Configurations/AdLookupValueConfiguration.cs

ErpWeb.Core/Admin/AdLookupTypes.cs
ErpWeb.Core/Admin/AdLookupTypeRegistry.cs
ErpWeb.Core/Admin/IAdLookupService.cs
ErpWeb.Core/Admin/AdLookupService.cs
ErpWeb.Core/Admin/AdLookupModels.cs

ErpWeb.UI/Admin/AdLookupList.razor
ErpWeb.UI/Admin/AdLookupList.razor.cs
```

Plus an idempotent DBA deployment script:

```text
scripts/alter-ad-lookup-value.sql
```

Do not add an EF migration unless the repository independently adopts EF migrations later.

## Modify

Likely:

```text
ErpWeb.Model/Data/AppDbContext.cs
ErpWeb.Core/CoreServiceCollectionExtensions.cs
ErpWeb.Core/Menus/MenuCodes.cs
menu XML / menu definition source used by current deployment

ErpWeb.Core/Inventory/IvInventoryRefService.cs
ErpWeb.UI/Inventory/Masters/IvWarehouseList.razor
ErpWeb.UI/Inventory/Masters/IvWarehouseList.razor.cs

ErpWeb.Core/Planning/PrMachineService.cs
ErpWeb.UI/Planning/Masters/PrMachineList.razor
ErpWeb.UI/Planning/Masters/PrMachineList.razor.cs

ErpWeb.Core/Planning/PrWorkCentreService.cs
ErpWeb.Core/Planning/PrHierarchyService.cs
ErpWeb.UI/Planning/Masters/PrWorkCentreList.razor
ErpWeb.UI/Planning/Masters/PrWorkCentreList.razor.cs

ErpWeb.Core/Purchase/PoSupplierService.cs
ErpWeb.Core/Purchase/IPoSupplierLookupService.cs
ErpWeb.Core/Purchase/PoSupplierLookupService.cs
ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor
ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor.cs

ErpWeb.Core/Security/CompanyService.cs

# No SaCust/Sales Analysis source switch in the initial approved implementation.
```

Tests under the existing Admin/Inventory/Planning/Purchase/Sales test structures.

The coding agent must re-check exact current file names before editing.

---

# 32. Strict Implementation Phases / Gates

## Phase 1 — Infrastructure

Implement:

- entity/configuration;
- DbSet;
- migration;
- type registry;
- service;
- DI;
- maintenance menu/page;
- concurrency/audit;
- tests.

### Gate 1

PASS only if:

- idempotent SQL deployment script passes on a representative database;
- script rerun is safe/no-op where appropriate;
- app builds;
- generic CRUD/activate/deactivate tests pass;
- tenant isolation passes;
- permissions work;
- no business module consumer changed yet.

---

## Phase 2 — Warehouse Type

Implement:

- seeds;
- free-text backfill;
- Warehouse dropdown;
- server validation.

### Gate 2

PASS only if:

- Warehouse CRUD passes;
- legacy values preserved;
- stock/costing regression passes;
- Warehouse Type has zero posting behavior.

---

## Phase 3 — Planning Classifications

Implement:

- MACHINE_TYPE;
- WORK_CENTRE_CLASS;
- backfill;
- UI;
- service validation.

### Gate 3

PASS only if:

- planning master tests pass;
- scheduler regression passes;
- no Work Order/BOM/routing behavior changes.

---

## Phase 4 — Supplier Classifications

Implement:

- SUPPLIER_TYPE;
- SUPPLIER_SUBGROUP;
- Supplier Category → existing `PoCategory`;
- backfill;
- validation/UI.

### Gate 4

PASS only if:

- Supplier master passes;
- supplier e-Invoice regression passes;
- PR/PO/GR flow passes;
- no payment/tax/currency behavior changed.

---

## Phase 5 — Customer Industry / Channel / Source

**DEFERRED — DO NOT IMPLEMENT IN THIS CHANGE SET.**

Keep current `IvMSCode` lookup and Sales Analysis behavior unchanged.

### Gate 5

PASS only if repository diff confirms:

- no customer Industry/Channel/Source source switch;
- no change to invoice snapshot behavior;
- no change to Sales Analysis grouping/filtering/description resolution;
- `IvMSCode` remains untouched by application code in this project.

---

## Phase 6 — Final Regression

Run full build and targeted suites.

The agent must produce:

```text
Changed files
Migration summary
Backfill counts by LookupType
Tests executed
Tests passed/failed
Known remaining legacy lookup areas
```

### Final Gate

APPROVED only if all phases pass.

---

# 33. MUST NOT

The AI coding agent must NOT:

- turn workflow statuses into editable lookup values;
- move FIFO/WAC/Standard Cost methods into the lookup table;
- change posting or rollback logic;
- change inventory quantities;
- change costing formulas;
- change reservation behavior;
- change Work Order scheduling logic;
- change BOM quantity logic;
- change Purchase receipt costing;
- change Sales issue/return costing;
- delete or repurpose `IvMSCode`;
- migrate `PAYCODE` in this plan;
- migrate `STATE` in this plan;
- migrate `TAX` in this plan;
- replace `SaPaymentTerm`;
- replace `SaTaxGroup`;
- replace `MsUom`;
- replace `PoCategory`;
- replace hierarchical masters;
- add hidden behavior based on Warehouse Type;
- hard-delete lookup values;
- accept arbitrary LookupType from UI;
- trust CompanyCode supplied by the client;
- silently remap legacy free text to guessed standard codes;
- modify consumer historical transaction rows;
- auto-assign WarehouseType when it is currently optional;
- require `ADMIN_LOOKUPS` permission merely to populate a business-screen dropdown;
- call a second DbContext from inside company provisioning to seed lookup defaults;
- use an EF migration workflow that the repository does not currently use;
- switch Customer Industry/Channel/Source away from `IvMSCode` in the initial implementation.

---

# 34. Acceptance Criteria

Implementation is accepted when:

1. one reusable generic configurable lookup infrastructure exists;
2. it is company scoped;
3. Warehouse Type is a dropdown, not free text;
4. Machine Type is a dropdown, not free text;
5. Work Centre Class is a dropdown, not free text;
6. Supplier Type is a dropdown;
7. Supplier Subgroup is a dropdown;
8. Supplier Group uses existing `PoCategory` through an assignment lookup that does not require Category-maintenance permission;
9. Customer Industry/Channel/Source remain on current `IvMSCode` in this implementation;
10. Sales Analysis Industry/Channel/Source behavior is unchanged;
11. existing data remains resolvable;
12. inactive values remain historically readable without becoming newly selectable;
13. every lookup family enforces its real consumer-column maximum length;
14. code values are stable;
15. concurrency/audit/security follow project standards;
16. no costing/posting/stock/WIP/scheduling behavior changes;
17. no existing consumer row is mass-rewritten;
18. database deployment follows the repository's idempotent SQL-script convention;
19. company default seeding participates in the existing company-provisioning transaction;
20. all phase gates pass.

---

# 35. Review Decision

**APPROVED FOR IMPLEMENTATION — 10/10**

This revised plan gives `productionv2` a reusable classification-master architecture while preserving the currently shipped ERP behavior. The initial deployment is intentionally limited to descriptive free-text classifications and does not change any existing transaction/status/costing/scheduling/customer-analytics authority.

The key design decision is deliberate:

> **Do not create one table for every simple business classification, but also do not put every enum/status into one generic table.**

Use `AdLookupValue` only where users should be able to add, rename, deactivate and report on descriptive classifications without recompiling the application.
