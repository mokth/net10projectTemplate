BOM explosion/structure, snapshot/refresh, SQL schema/scripts, UI/help
Testing scope: No new unit tests requested. Use compile checks + manual functional verification only.

1. Goal

Change the current model from:

Product + BOM As-of Date
        ↓
ACTIVE Product Definition revision effective on that date
        ↓
Work Order snapshot

to:

Product
   +
Selected Production Definition
        ↓
Current ACTIVE revision of that definition
        ↓
Work Order snapshot

Example:

FG001
 ├─ STANDARD     → Active Rev 3
 ├─ LINE-B       → Active Rev 1
 └─ SUBCON       → Active Rev 2

When creating a Work Order:

Product                : FG001
Production Definition  : LINE-B
Planned Qty            : 1,000
Planned Start          : 10-Oct-2026

The system resolves:

FG001 + LINE-B
      ↓
ACTIVE revision
      ↓
LINE-B Rev 1
      ↓
freeze complete definition into Work Order snapshot

BOM as-of date, EffectiveFrom, and EffectiveTo are no longer part of the normal Product Definition / Work Order selection model.

2. Repository Findings That Drive This Plan

The current production branch assumes one Product Definition timeline per product.

Important current dependencies:

PrBomHdr identity is currently:

CompanyCode

ProdCode

Version

PrBomHdrConfiguration has unique:

(CompanyCode, ProdCode, Version)

PrProductDefService.SearchAsync() groups Product Definitions by ProdCode only and returns one latest row per product.

GetAsync(), GetStructureTreeAsync(), CreateNewVersionAsync(), ActivateAsync(), delete handling, structure caching and navigation all identify Product Definition mainly by product + version.

Activation currently uses EffectiveFrom / EffectiveTo and supersedes overlapping ACTIVE versions for the whole product.

ProductDefinitionSnapshotLoader.ResolveRevisionAsync() resolves:

company

product

as-of date

ACTIVE status

Work Order currently sends SnapshotAsOfDate / DefinitionEffectiveDate into WorkOrderSnapshotBuilder.

Work Order snapshot hash includes:

DefinitionEffectiveDate

SourceEffectiveFrom

Product Definition source hash includes:

EffectiveFrom

EffectiveTo

PrBomExplorer exposes an As-of date field and BomExplosionService uses effective-date selection.

Multi-level Product Definition structure currently follows MAKE / PHANTOM items by product code and resolves the latest/date-applicable BOM. Once multiple definitions exist for the same component product, this becomes ambiguous.

PrDefBOM already has a useful SupplySource value:

PURCHASED

INTERNAL_ROUTE_WIP

SEPARATE_PRODUCT_DEFINITION

EXTERNAL_SUPPLY

This should be used to make multi-level Product Definition selection deterministic.

Work Order already freezes the exact source revision through fields such as:

SourceBomHdrId

SourceBomVersion

SourceProductDefinitionRevisionId

snapshot children

source/snapshot hashes

This is good and should be preserved.

ProductionWorkOrderService currently contains both the current hierarchy snapshot builder and an older BOM explosion / prepared-snapshot path. This change should avoid maintaining two competing definition-selection engines.

3. Target Business Model

3.1 Product Definition identity

A Product Definition becomes:

CompanyCode
+ ProdCode
+ DefinitionCode

A revision becomes:

CompanyCode
+ ProdCode
+ DefinitionCode
+ Version

Example:

Product

Definition

Name

Revision

Status

Default

FG001

STANDARD

Standard Production

3

ACTIVE

Yes

FG001

LINE-B

Line B Production

1

ACTIVE

No

FG001

SUBCON

Subcontract

2

ACTIVE

No

All three definitions may be ACTIVE simultaneously.

3.2 Revision rule

Within the same Product + Definition:

FG001 / STANDARD / V1 → SUPERSEDED
FG001 / STANDARD / V2 → SUPERSEDED
FG001 / STANDARD / V3 → ACTIVE

Only one ACTIVE revision is allowed for:

CompanyCode + ProdCode + DefinitionCode

But this is valid:

FG001 / STANDARD / V3 → ACTIVE
FG001 / LINE-B   / V1 → ACTIVE
FG001 / SUBCON   / V2 → ACTIVE

3.3 Default definition

Each product should normally have one default ACTIVE definition.

Example:

FG001 / STANDARD → IsDefaultDefinition = true
FG001 / LINE-B   → false
FG001 / SUBCON   → false

Work Order behavior:

If one ACTIVE definition exists → auto-select it.

If several ACTIVE definitions exist and one is default → auto-select default.

User may change to another ACTIVE definition.

If data is inconsistent and several exist with no default → require user selection instead of guessing.

The service should normally prevent the inconsistent state by automatically making the first activated definition the default.

4. Database Design

Keep the existing PrBomHdr revision table. Do not introduce a SAP-style extra definition master table for this requirement.

4.1 PrBomHdr

Add:

DefinitionCode        nvarchar(30)  NOT NULL
DefinitionName        nvarchar(100) NULL
IsDefaultDefinition   bit           NOT NULL DEFAULT (0)

Remove from active application semantics:

EffectiveFrom
EffectiveTo

After migration and application deployment are stable, drop those two columns.

New indexes

Replace:

UQ_PrBomHdr_Company_Prod_Version
(CompanyCode, ProdCode, Version)

with:

UQ_PrBomHdr_Company_Prod_Definition_Version
(CompanyCode, ProdCode, DefinitionCode, Version)

Add unique filtered index:

UX_PrBomHdr_OneActiveRevision
(CompanyCode, ProdCode, DefinitionCode)
WHERE Status = 'ACTIVE'

Add unique filtered index:

UX_PrBomHdr_OneActiveDefault
(CompanyCode, ProdCode)
WHERE Status = 'ACTIVE'
  AND IsDefaultDefinition = 1

Add lookup index:

IX_PrBomHdr_Company_Prod_Definition_Status
(CompanyCode, ProdCode, DefinitionCode, Status)

Add a check ensuring DefinitionCode is not blank.

For fresh databases, make PrBomHdr.Status default to DRAFT so the SQL default matches the current application model.

4.2 PrDefBOM

Add:

ComponentDefinitionCode nvarchar(30) NULL

Meaning:

When this material is supplied by SEPARATE_PRODUCT_DEFINITION, this is the Product Definition that produces the component.

Example:

FG001 / STANDARD
  BOM:
    WIP001
      SupplySource = SEPARATE_PRODUCT_DEFINITION
      ComponentDefinitionCode = STANDARD

or:

FG001 / LINE-B
  BOM:
    WIP001
      SupplySource = SEPARATE_PRODUCT_DEFINITION
      ComponentDefinitionCode = LINE-B

Validation

When:

SupplySource = SEPARATE_PRODUCT_DEFINITION

then:

ComponentDefinitionCode is required

and an ACTIVE Product Definition must exist for:

Company + Component ICode + ComponentDefinitionCode

For these supply sources:

PURCHASED
INTERNAL_ROUTE_WIP
EXTERNAL_SUPPLY

ComponentDefinitionCode must be null.

This makes multi-level Product Definition explosion deterministic.

4.3 PrWorkOrder

Add:

SourceDefinitionCode nvarchar(30) NOT NULL
SourceDefinitionName nvarchar(100) NULL

Keep:

SourceBomHdrID
SourceBomVersion
SourceProductDefinitionRevisionID
DefinitionSourceHash
DefinitionSourceHashVersion
SnapshotRevision
SnapshotHash
SnapshotHashVersion

These already provide strong historical traceability.

Remove from active application semantics:

SnapshotAsOfDate
SourceEffectiveFrom

After migration/application cutover, drop both columns.

Why keep both SourceBomHdrID and SourceProductDefinitionRevisionID for now?

They are currently redundant, but both are already used by Work Order concurrency, refresh and snapshot code. Removing one at the same time would increase risk without helping this feature.

Do not broaden this change unnecessarily.

4.4 PrWorkOrderMaterial

Add:

ComponentDefinitionCode nvarchar(30) NULL

Copy it from the exact frozen PrDefBOM line.

This is important for later:

child Work Order generation;

MRP;

WIP traceability;

planned supply;

multi-level production analysis.

It also keeps a material substitution deterministic when the replacement material is supplied by a separate Product Definition.

5. Migration Strategy

Because the system is still under development, clean the model now, but use a safe staged SQL migration.

Create:

scripts/alter-product-definition-multiple-definitions.sql

Stage A — additive migration

Add PrB