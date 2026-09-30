# Product Definition / Multi-Level BOM

## Purpose

Maintain manufacturing **Product Definition** (Bill of Materials) with recursive multi-level structure.

Menu: **Planning → Master → Product Definition** (`PLN_PRODUCT_DEF`).  
Explorer: `/planning/product-definitions/explode/{ProdCode}`.

Tables: `dbo.PrBomHdr` (versioned header), `dbo.PrDefBOM` (direct-child lines), plus version-owned
`dbo.PrBomOperation`, `dbo.PrBomMachineOption`, and `dbo.PrBomLabourStandard` routing/resource tables.

## Design locks (mandatory)

1. **Base quantity:** `PrBomHdr.BaseQty` / `BaseUom`. Line `StdQty` is qty to produce `BaseQty` of parent.  
   `RequiredQty = IvQty.Round(ProductionQty / BaseQty × StdQty × (1 + ScrapPercent/100))`.
2. **BOM scope:** **Company + Product + Version only.** `BranchCode` / `LocationCode` are leftover write stamps — never used for BOM selection.
3. **Version lifecycle:** `DRAFT` → `ACTIVE` → `SUPERSEDED` (normal). `INACTIVE` for intentional disable.
   ACTIVE/SUPERSEDED/INACTIVE versions are immutable; change starts with **New Version**, which clones the complete BOM and route aggregate.
4. **Effective windows:** inclusive From, exclusive To (`AsOfDate < EffectiveTo`). At most one applicable ACTIVE BOM per Company+Product for activation. Explosion date resolution selects among **ACTIVE or SUPERSEDED** headers by effective window (so historical dates still resolve after succession). DRAFT/INACTIVE are ignored unless an explicit Version is requested.
5. **MAKE / PHANTOM without applicable BOM:** explosion **hard ERROR**.
6. **Warehouse:** BOM line warehouse = **default/preferred source**; Production/Material Issue is authoritative for actual issue WH.
7. **Production snapshot (Phase 7):** released/created Production Orders must store `BomHdrId`, `Version`, `BaseQty`, `BaseUom`, `AsOfDate`, and a full copy of **Production Issue Requirement** lines. Revising the BOM must not change historical orders.

## Architecture

Each Make/Phantom item owns its own BOM of **direct children only**. Do **not** persist exploded descendants. Do **not** use Level1/2/3 columns.

Routing is owned by the same `PrBomHdr` revision as the direct-child BOM. A route operation retains the
legacy work-centre/output boundary and process sequence; machine options retain explicit seconds and labour
retains the legacy cost-per-output-unit basis. Creating a new version clones BOM, route, machines and labour.
Work Orders snapshot routing only from the exact header selected by BOM date/version resolution. Unversioned
`PrDefProcess`/`PrDefMachine` reads remain only as a compatibility fallback for definitions not yet migrated.

**Product Definition entry** shows a composition **DxTreeList** rooted at the route product (`StructureRootProdCode`). Selecting a Make/Phantom and adding under it switches the in-page **edit owner** (`CurrentOwnerProdCode`) after a dirty-guard; **Save still calls `SaveAsync` for one product header at a time**. Tree keys identify BOM-line occurrences (never bare ItemCode). Unsaved `CurrentModel.Lines` replace persisted direct children of the current owner in the tree display.

Reusable sub-assemblies: Product A → BB and Product C → BB share one `PrBomHdr` for BB.

Structure read API: `IPrProductDefService.GetStructureTreeAsync` (same version resolution as `GetAsync`). Explorer explosion remains `IBomExplosionService`.


## MfgType (`IvStockMaster.MfgType`)

`BUY` | `MAKE` | `PHANTOM` (default `BUY`). Separate from `IType` FG/RM.

| State | Save BOM | Explode |
|-------|----------|---------|
| MAKE + active BOM | OK | Recurse |
| MAKE + no BOM | — | ERROR |
| BUY + BOM | Auto BUY→MAKE on first save | — |
| PHANTOM + BOM | OK | Explode through (not stocked issue target) |
| PHANTOM + no BOM | — | ERROR |

## Explosion modes (`IBomExplosionService`)

| Mode | Purpose |
|------|---------|
| `StructuralTree` | Full hierarchy (Explorer) |
| `MaterialRequirement` | BUY + MAKE extended qtys (Phantom through); planning/MRP |
| `ProductionIssueRequirement` | Direct BUY+MAKE after Phantom through — **no Make descendants** (prevents double consumption) |

Example Production Issue for A: AA, BB, CC only. Producing BB separately issues BBA, BBB.

## Quantity / scrap / rounding

- Precision: `IvQty.Scale = 4`, AwayFromZero.
- Do not round StdQty/BaseQty before multiply; round once per exploded node.
- Scrap % compounds per level. Yield % and fixed scrap qty are **not** implemented (reserved).

## Inventory boundary

**Product Definition never posts inventory.** Saving/editing/deleting BOM changes master data only.

## Future Production Order (binding contract)

On create/release:

1. Resolve BOM as-of order date (or explicit version).
2. Call `ProductionIssueRequirement` mode.
3. **Snapshot** header refs + issue lines onto the order.
4. Never live-re-read current BOM for a released order.

## Costing

BOM structure supports future roll-up costing. **Do not assume** BOM cost = raw materials only (labor/machine/overhead/subcontract may apply later).

## Schema scripts

- Greenfield: [`scripts/create-prdefbom.sql`](../../scripts/create-prdefbom.sql)
- Upgrade: [`scripts/alter-prdefbom-multilevel.sql`](../../scripts/alter-prdefbom-multilevel.sql)
- Version-owned routing: [`scripts/create-product-definition-routing.sql`](../../scripts/create-product-definition-routing.sql)

## Permissions

ACCESS / ADD / EDIT / DELETE on `PLN_PRODUCT_DEF`.

See also: [product-definition-bom-phase0-findings.md](product-definition-bom-phase0-findings.md).
