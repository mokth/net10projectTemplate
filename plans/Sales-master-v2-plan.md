> **STATUS — CHANGE LOG. Do not implement from this file.**
> This file is the review and correction *history* for the Sales item-family plan: the original
> correction plan, Review v1, Review v2 + Appendix A, and the Review v4 decision-closure record.
> The **implementation document** is `plans/sales-item-family-v2-plan.md` — a clean, decision-closed
> plan whose §9 decision register must be signed before Phase 1. Anything in this file that conflicts
> with that plan is superseded by it.

# Plan: Revise the Sales Master v2 plan (Item Family)

The v2 plan's skeleton is right — binary Phase 0 gate, defect register, D2 decisions, shared contract, phases 0–7, test matrix — and it inherits v1's discipline. But four premises are wrong or missing, so the document needs correcting before any code. Four owner decisions set the direction: reuse the existing item lookup, let Phase 0 settle `IvMas` vs `IvStockMaster` (preferring the latter), add a composite-key decision, and align price visibility with the shipped `VIEW_COST` precedent plus the `SaCust.CustPriceCode` wiring.

## Basis — verified findings

- **D2-18 / §7 are wrong.** The Blazor item master exists as `IvStockMaster`: entity `IvStockMaster.cs`, config `IvStockMasterConfiguration.cs` (`ToTable("IvStockMaster")`), `DbSet<IvStockMasters>` (`AppDbContext.cs:39`), full CRUD in `IvStockMasterService.cs` (every gate on `MenuCodes.InventoryItemMaster`), UI `IvStockMasterList.razor` / `IvStockMasterEntry.razor` at `/inventory/items`, registered in `menus.xml:7` as "Item Master", tenant stamp `InventoryLeftoverSite.Apply(IvStockMaster, …)` (`InventoryTenantContext.cs:140`), FK `(CompanyCode, ICode) -> IvStockMaster` (`docs/ivlot_explain.md:79`). Lookup already exists: `IIvInventoryLookupService.SearchStockMastersAsync`, `IvStockMasterRepository.ListActiveForLookupAsync/SearchActiveAsync/GetByBarcodeAsync`, `IvStockMasterPicker.razor`, `IvStockMasterSearchPopup.razor`. Legacy's item table is `IvMas`.
- **Key plumbing supports one natural part + optional parent only.** `SaRefListPageBase.cs:309` `Key(code, rowVersion, parentCode)`; `IvMasterResults.cs:158` `IvMasterKeyToken { Code, RowVersion, ParentCode }`; `SaKeyedRefListPageBase` has no row version and no activate buttons.
- **D2-14 contradicts the shipped precedent.** `PermissionCodes.ViewCost = "VIEW_COST"`; `PoPr`/`PoOrder` treat it as visibility only and the server still persists real prices (`PoPrServiceTests.cs:686`); permission rows seeded by `scripts/init-menu-access.sql:117`.
- **The one real consumer link is missing.** `SaCust.CustPriceCode` exists (`SaCust.cs:52`, `SaCustConfiguration.cs:59` maxlength 20, written by `SaCustService.cs:598/793/917`) and is a free-text `DxTextBox` in `SaCustEntry.razor:740` (read-only display at `:213`). Same shape as v1 D-6 (`SubGroupCode`), which was fixed with master + lookup + `ValidateXAssignmentAsync(code, existingCode, ct)`.
- **No Blazor consumer** of `CustPrice` / `SaItemCust` / `DisGroupItem` exists today.
- **Export plumbing to reuse:** `SaMasterRefExportWorkbooks.cs`, `SaMasterRefExportEndpoints.cs`, `SaRefListPageBase.ExportRoute`.
- **Menu trap:** `menus.xml` is reconciled every startup and unknown codes are soft-disabled; a new menu needs `scripts/init-<feature>-menu.sql` too, guarded by `ErpWeb.Tests/MenuDeploymentParityTests.cs`.
- **House style** (`sales_master_crud_e303c8f0.plan.md`, `supplier_master_plan.md`): fenced `---` YAML at line 1 with `name` / `overview` / `todos:` as an `id/content/status` list / `isProject: false`.

## Steps

### Phase A — correct `plans/Sales-master-v2-plan.md` (document work only)

1. Front matter to house style: fenced `---` YAML at line 1 with `name`, `overview`, `todos` as an `id/content/status` list, `isProject: false`; delete the `INTENDED PATH` / `BLOCKER` / `STATUS` preamble; rename todo `regroup-reconcile` → `dis-group-reconcile`; make the declared path match the on-disk `plans/Sales-master-v2-plan.md`.
2. Delete D2-18. Rewrite §7 REUSE/MISSING to name the real item master — `IvStockMaster` entity/config/DbSet, `IvStockMasterService`, `/inventory/items` list + entry, `IvStockMasterPicker`, `IvStockMasterSearchPopup`, `IIvInventoryLookupService.SearchStockMastersAsync`, `IvStockMasterRepository.ListActiveForLookupAsync` — and drop "read-only item lookup" from §2 IN.
3. Add the missing Phase 0 questions: (a) does live `ERPWeb` hold `IvMas`, `IvStockMaster`, both or neither, and which is the `ICode` authority; (b) every existing writer of `SaCust.CustPriceCode` (entry page, imports, SO copy); (c) Blazor consumers of legacy `Status='NEW'`, `IvCustPriceGroup.Editable`, `SaDisGroupItem.GroupName`, `GroupLevel`, `Discount1/2/3`, `DiscountType1`, `EffectPrice`, `IClass`; (d) line-level `ICode` / `UOM` / `IClass` value sets in DATA; (e) whether `scripts/` already names these tables.
4. Decisions: make D2-7 / D2-9 / D2-13 **conditional** on the EXISTS/ABSENT branch (a live column cannot be "not created"); add D2-21 (composite-key model — token/row/`DeleteCheckResult`/page base for 3+ part keys), D2-22 (`VIEW_PRICE` as a `PermissionCodes` constant in the `ViewCost` style — visibility only, server never trusts client price, denial still persists real values), D2-23 (`SaCust.CustPriceCode` lookup + server-side validation + `ISaCustLookupService.ValidateCustPriceCodeAssignmentAsync`, v1 D-6 parity), D2-24 (no SO/INV price-resolution consumer in v2; cascade + tie-break documented in the spec with a named follow-up).
5. Defect #3: state the replacement default (seed `UnitPrice` from the selected item's selling price, subject to D2-22) instead of a bare "remove".
6. Rewrite §13: name `scripts/init-sales-item-family-menu.sql`, the `MenuSyncService` soft-disable trap, `MenuDeploymentParityTests`, the export reuse pair (`SaMasterRefExportWorkbooks.cs`, `SaMasterRefExportEndpoints.cs`, `SaRefListPageBase.ExportRoute`), and the ADMIN-smoke-before-grant order.
7. §10: restate Phase 0 PASS/FAIL as binary with an explicit FAIL action (stop; update this plan before code); renumber tests to repo convention and name the test files; move the four conditional §14 veto items after Phase 0.
8. Tie `IvCustPrice.UOM` and `SaDisGroupItem.IClass` to the existing `IvClass` (`/inventory/classes`) and `IvUom` (`/inventory/uoms`) masters via `IvCodeComboBox`, and fix §7's method names (`ListXForAssignmentAsync`, `ValidateXAssignmentAsync` — there is no `GetItemAsync`).

### Phase B — execute the corrected plan (phases 0–7)

9. Phase 0 gate (Phase A step 3 questions) → `ErpWeb/docs/sales-item-family-phase0-findings.md`, binary PASS.
10. Phase 1: DDL + entities/configs/DbSets + tenant/audit plumbing + DI + the D2-21 key-model work + ADMIN smoke menu. *(no lookup service — deleted)*
11. Phase 2: `IvCustPriceGroup` master plus the `SaCustEntry` price-group box conversion (D2-23).
12. Phase 3: `IvCustPrice` lines, one transaction with the header, `VIEW_PRICE` enforcement.
13. Phases 4–5: `SaItemCust`, then `SaDisGroupItem` + read-only listing + the qty-band overlap rule inside the service transaction.
14. Phase 6: `SaDisGroup` gap list only (verification, no rebuild). Phase 7: menus, permissions, `scripts/init-sales-item-family-menu.sql`, export endpoints, docs.

Each phase green (build + tests) before the next.

## Relevant files

- `plans/Sales-master-v2-plan.md` — the document being revised (Phase A)
- `docs/sales-master-plan.md`, `docs/sales-item-family-logic.md`, `ErpWeb/docs/sales-master-phase0-findings.md` — v1 record, logic spec of record, Phase 0 format template
- `ErpWeb.Core/Sales/ISaSalesRefService.cs`, `SaSalesRefService.cs` — service contract to extend
- `ErpWeb.Core/Sales/ISaCustLookupService.cs`, `SaCustLookupService.cs` — lookup template + D2-23 addition
- `ErpWeb.Core/Sales/SaMasterRefExportWorkbooks.cs`, `ErpWeb/Sales/SaMasterRefExportEndpoints.cs` — export reuse
- `ErpWeb.UI/Admin/Master/SaRefListPageBase.cs`, `SaKeyedRefListPageBase.cs`, `ErpWeb.UI/Sales/Masters/SaCodeRefListPageBase.cs` — shells (+ D2-21 changes)
- `ErpWeb.Core/Inventory/IvMasterResults.cs` — `IvMasterKeyToken` / `IvMasterOperationResult` (+ D2-21 changes)
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor`, `ErpWeb.Core/Inventory/IvInventoryLookupService.cs` — item lookup reuse
- `ErpWeb.UI/Sales/Masters/SaCustEntry.razor` (~L740 free-text box, ~L213 read-only display) — D2-23
- `ErpWeb.Core/Inventory/InventoryTenantContext.cs` — one `Apply` overload per new entity
- `ErpWeb/Menus/menus.xml`, `ErpWeb.Core/Menus/MenuCodes.cs`, `ErpWeb.Core/Menus/PermissionCodes.cs`, `scripts/init-menu-access.sql`
- `ErpWeb.Tests/MenuDeploymentParityTests.cs` — build-time menu gate

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` clean after each phase.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` green, including the SQL Server concurrency suite via the `ConnectionStrings:SqlServerTestConnection` scratch DB and `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`.
3. Phase A: re-read the revised plan against the findings list — every item addressed or explicitly deferred.
4. DDL script run twice on a scratch DB (second run a no-op); column matrix diffed against the script.
5. `MenuDeploymentParityTests` green with the new menu codes.
6. Per-screen manual smoke including item / customer / price-group pickers; negative smoke (no `VIEW_PRICE`, no ADD, direct URL); no §6 defect reproduces; shipped `SaDisGroup` list still works.

## Decisions

- **Item lookup:** reuse `IvStockMasterPicker` + the existing lookup; D2-18 deleted.
- **Item table authority:** Phase 0 determines it; prefer `IvStockMaster` if it exists live, document `IvMas` as legacy-only.
- **Multi-part keys:** new decision D2-21 plus a design step in Phase 1 (token / row / `DeleteCheckResult` / page base).
- **Price visibility:** `VIEW_PRICE` as a `PermissionCodes` constant in the `ViewCost` style — visibility only, never blocking persistence.
- **Consumer wiring:** `SaCust.CustPriceCode` is wired (D-6 pattern); no SO/INV price-resolution consumer in this scope.

## Further considerations

1. Whether the price-group lookup conversion lands in Phase 2 (with the header master) or a separate step after Phase 3 so the lookup can validate against real lines. Recommendation: Phase 2 — the master is useless to `SaCust` until then.
2. Whether `IvCustPrice` lines stay a standalone list page (needs the D2-21 composite key) or become a child grid inside the header entry screen. Recommendation: standalone, matching legacy `CustPriceGroupItemsView` and the four-list count in §2.
3. Whether to keep all five entry screens or collapse `SaDisGroupItem`'s entry and read-only listing into one screen. Recommendation: keep both — the read-only listing is `CustItemDiscList`'s tenant/item-scoped replacement (defect #15).

---

# Plan review — ERP implementation-plan review (2026-09-15)

## Scope and method

The document under revision is **not on disk**: the only file matching `plans/*ales-master-v2*` is this one, which holds the *correction* plan (verified: 76 lines / 10,440 bytes). So this review covers (a) the v2 premises **as described by this plan's Basis and Steps**, and (b) every underlying fact, re-verified in this session against code, `docs/sales-item-family-logic.md`, `ErpWeb/docs/sales-master-phase0-findings.md`, `ErpWeb/docs/sales-invoice-recon.md` and `scripts/`. Facts checked here are marked *(verified)*; anything resting only on the v2 plan's own summary is marked *(described)*.

Classification: 🔴 **BLOCKER** do not implement until resolved · 🟠 **HIGH** likely to cause ERP/data/architecture problems · 🟡 **MEDIUM** should improve before implementation · 🔵 **LOW** quality/maintainability · 🟢 **GOOD** keep as-is.

**Verdict: not yet implementation-ready** — the phase skeleton, the Phase-0 gate and the reuse-first posture are right, but three Phase-1 axes are under-specified (key model, concurrency level, DDL create-vs-alter branch), one decision is aimed at a file that does not exist, and the *price/discount relationship model* — the part that decides whether these masters are actually useful — is absent. This needs ~5 closed decisions and ~4 extra Phase 0 facts, not a redesign.

## Findings

### R-1 🔴 BLOCKER — the v2 document has no home, and its name collides with this plan

**Problem** *(verified)*. This file's own Phase A step 1 says "make the declared path match the on-disk `plans/Sales-master-v2-plan.md`", and the Relevant-files list calls `plans/Sales-master-v2-plan.md` "the document being revised". But that path *is* this conversion plan. On Windows the two names are the same file, so Phase A cannot be executed as written: correcting step 1 would overwrite the instructions that describe the correction.
**Why it matters.** Phase A is the gate to all Phase B code. A gate aimed at a non-existent/colliding path means the document implementers actually follow is never produced — and it contradicts the plan's own "one authoritative writer per concept" principle that it applies (rightly) to item masters.
**Recommended change.** Give the v2 document a non-colliding path (e.g. `plans/sales-item-family-v2-plan.md`), materialise it from its real sources (prior chat output + `docs/sales-item-family-logic.md` + the v1 house style), then re-point this file's step 1 and Relevant-files list at it and keep this file as the change log.
**Where.** Phase A step 1; Relevant files.

### R-2 🔴 BLOCKER — item-master authority must be an invariant, not "prefer `IvStockMaster`"

**Problem** *(verified)*. The Decisions section says "prefer `IvStockMaster` if it exists live, document `IvMas` as legacy-only". On the Blazor side there is exactly one item master already: `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs` (PK `CompanyCode`+`ICode`, `IsActive` → column `Active`, `RowVersion`, `SellingPrice`, `SellingUom`, `IClass` → column `IClass`), plus `IvStockMasterService`, `/inventory/items`, `IvStockMasterPicker`, `IIvInventoryLookupService.SearchStockMastersAsync`. A whole-solution grep for `IvMas` finds **zero** references outside documentation — no entity, no DbSet, no config. So the real risk is not Blazor-vs-Blazor; it is the *external* writer set (legacy `ERPV55` WebForms, `ImportCustProduct`/`UploadCustPrice`/`UpdateSalesPrice`, mobile `DataService`) on whatever database the Blazor app points at.
**Why it matters.** If new Sales Master FKs target `IvStockMaster` while a legacy process keeps writing `IvMas`, the masters validate items that the future transaction engine cannot resolve — a technically correct but operationally inconsistent ERP. "Prefer" leaves that decision to whoever writes the FK first.
**Recommended change.** Phase 0 returns a **binary per-table statement**: `IvMas` present/absent in the live ERPWeb DB, `IvStockMaster` present/absent, row counts, and the named writer of each. Then a closed decision: *one* item master for all new Sales Master references, plus an explicit disposition for `IvMas` (absent → no action; present → declared legacy/read-only, with the list of Blazor code paths that must never write it). Any unresolved answer = Phase 0 FAIL.
**Where.** Phase 0 questions; Decisions ("Item table authority"); Phase 1 FKs.

### R-3 🟠 HIGH — masters are specified, but the *price/discount resolution contract* is not

**Problem** *(verified)*. Every operand of the future engine already exists, but the rule that combines them is unwritten:

| Operand | Live today |
|---|---|
| `SaCust.CustPriceCode` (price group pointer) | free text box — hence D2-23 |
| `SaCust.GroupDiscount` (discount group pointer) | already validated + delete-blocked (`ISaCustLookupService.ValidateDisGroupAssignmentAsync`, `SaSalesRefService.cs:3184`) |
| `SaCust.PriceMethod` | **verbatim legacy string**: `SaCustPaymentOptions.PriceSelling = "FOLLOW SELLING PRICE X DISCOUNT"`, `PriceDealer = "FOLLOW DEFAULT DEALER PRICE"`; read by `SaCustEntry.razor.cs` via `ContainsToken(..., "DEALER")` |
| `SaCust.DiscountMethod` | `SaCustPaymentOptions.DiscountJoin/DiscountSplit` (`JOIN`/`SPLIT`) |
| legacy `SaDisGroupItem.EffectPrice` | `DEALER` \| `SELLING` — the *same* token family as `PriceMethod` |
| legacy `SaDisGroupItem.Discount/DiscountType` + `Discount1/DiscountType1` | two slots, pct-or-amount each |
| line model + engine | `SaSoDetail/SaDoDetail/SaInvoiceDetail/SaCdnDetail.ItemDiscount..6` + `ItemDiscAmount/ItemDiscAmount1`, computed by shipped `SaInvoiceCalc.CalculateDiscountPerUnit` (JOIN replaces the stack, otherwise sequential; **no SPLIT branch**; `CustDiscount` unused — `ErpWeb/docs/sales-invoice-recon.md:317-324`) |

**Why it matters.** "No SO/INV price-resolution consumer in v2" is a correct scope call, but it is not the same as "no rule in v2". Masters are only useful to a future engine if they store *deterministic, non-ambiguous* inputs. Unresolved today: base-price precedence (item `IvStockMaster.SellingPrice` vs `SaItemCust.UnitPrice` vs `IvCustPrice.SellingPrice`), which field decides it, whether `EffectPrice` overrides `PriceMethod`, precedence when several `SaDisGroupItem` rows match one item/qty/date, whether a customer-specific row beats a global one, and the NULL-date rule — legacy has **two item-discount queries that disagree**: `SaCustomerBL.GetDiscountItem` tolerates NULL windows, `SalesDicountHelper.GetItemDiscount` excludes them.
**Recommended change.** Add a **Price & discount resolution contract** to the shared-contract section pinning: (1) the ordered input key `(CompanyCode, BranchCode?, Item, UOM, Qty, DocDate, CustCode, PayCode)`; (2) a numbered precedence table that terminates in "no match → 0"; (3) the unique/priority indexes that make each step deterministic; (4) `PriceMethod`/`DiscountMethod`/`EffectPrice` as *stored tokens* mapped to `SaCustPaymentOptions` constants, with an explicit winner when `EffectPrice` and `PriceMethod` disagree; (5) the multi-match tie-break and the single chosen date-window NULL rule (applied at consumption, never retro-fitted into data); (6) the exact `SaDisGroupItem` slot → line-slot mapping (percent slots vs amount slots), because `SaInvoiceCalc` treats them differently.
**Where.** Shared contract; D2-24 (widen from "no consumer" to "no consumer **but** the contract is pinned now"); new Phase 0 question (which legacy item-discount query is authoritative); Phase 3/5 rules.

### R-4 🟠 HIGH — `QtyFr = 0 AND QtyTo = 0` is a live row that can never match, and nothing decides it

**Problem** *(verified — `docs/sales-item-family-logic.md` §5)*. Both legacy lookups require `QtyFr <= qty AND QtyTo >= qty`, so a 0/0 band matches **no** quantity; the entry screen's special case only relaxes the overlap validator. Live 0/0 rows are expected (legacy Q6).
**Why it matters.** Porting the screen as-is lets an operator create a catch-all band that silently does nothing → under/over-discounted invoices later. "Fixing" it silently changes legacy pricing.
**Recommended change.** Close it as a decision — recommended: **reject 0/0 at save** with an explicit message, since the port carries no obligation to preserve the paradox; if Phase 0 finds live 0/0 rows, list them and either delete-after-review or keep read-only. Either way it is a service-side validation rule with a test.
**Where.** New Phase 0 fact + Phase 5 validation + test matrix.

### R-5 🟠 HIGH — `SaDisGroup`/`SaDisCust` are already shipped; the item family must not become their second writer

**Problem** *(verified)*. `SaDisGroup` and `SaDisCust` exist in Blazor (PK `(CompanyCode, GroupName, PayCode)`, Level B, script-created — `ErpWeb/docs/sales-master-phase0-findings.md`), while the legacy screens keyed on `GroupName`+`PayCode` with **no tenant** (defects 13/14) and the legacy `SaDisCust` shape differs from the Blazor one (Q7). Separately, legacy `SaDisGroupItem.GroupName` is **never written** by any screen and read by no consumer query.
**Why it matters.** Phase 6 says "gap list only, no rebuild" — good — but the plan never states the invariant that would keep it true. A ported `CustomerGroupDiscount` (or a `SaDisGroupItem → SaDisGroup` FK added "for completeness") re-creates exactly the two-writers-one-concept problem the plan is eliminating for items.
**Recommended change.** One explicit decision: Blazor `SaDisGroup`/`SaDisCust` remain the single writer/reader (no port of `CustomerGroupDiscount`); `SaDisGroupItem` is **not** wired to `SaDisGroup` — the `GroupName` column is either omitted from the new write path or kept as an ignored legacy column, documented as such. Add a regression test that the shipped `SaDisGroup`/`SaDisCust` screens still save/list.
**Where.** Decisions; Phase 6; Verification step 6.

### R-6 🟠 HIGH — for multi-part keys, use the shipped surrogate-key escape hatch, not a framework-wide token change

**Problem** *(verified)*. `IvMasterKeyToken { Code, RowVersion, ParentCode }` (`IvMasterResults.cs`) and `SaRefListPageBase.Key(code, rowVersion, parentCode)` give one natural part + an optional parent. But `PoPurItemList.razor.cs` already ships `Key(row.Id.ToString(), row.RowVersion)` against `PoPurItem.Id` (column `ID`, `ValueGeneratedOnAdd`, PK) — i.e. the house answer for a key that does not fit is a **surrogate PK surfaced through `Code`**. D2-21 as written ("token / row / `DeleteCheckResult` / page base for 3+ part keys") invites changing a token type used by ~30 list pages across Inventory, Purchase, Admin and Sales.
**Why it matters.** That is a high-blast-radius change to solve a problem four new screens have — and it is unnecessary. Legacy `SaDisGroupItem` already carries `ID` (IDENTITY); the other three tables have natural app keys (`(ICode, CustCode, SellingUOM, MOQ)`, `(CustPriceCode, ICode, UOM)`, `CustPriceCode`). If these are live legacy tables, their PK/identity **cannot** be re-shaped in place anyway (destructive), so the natural key must survive as a unique index regardless.
**Recommended change.** Restate D2-21 as a two-option decision with a recommended default: **(a) recommended** — keep/add a surrogate per table (`SaDisGroupItem.ID` reused; add `Id` to the others only where the live PK cannot be honoured), key the page on `Code = Id.ToString()` (shipped precedent), and keep the legacy natural key as a unique index + business identity in the UI; **(b) only if proven necessary** — extend `IvMasterKeyToken`, in which case the plan must enumerate every affected page base and its tests. `DeleteCheckResult` needs no change either way (`Ok(message)` already exists).
**Where.** D2-21; Phase 1 design step; Phase 3/5 page work.

### R-7 🟠 HIGH — RowVersion level (A vs B) is undecided for the new masters

**Problem** *(verified)*. Legacy item-family tables have **no `RowVersion` anywhere** (logic-spec defect 15); the only concurrency control was a display-only "another user's item" check. Two conventions ship: **Level A** (real DB `RowVersion`) — `SaCust`, `SaCustGroup`, `SaCustType`, all Purchase masters, `IvStockMaster`; **Level B** (computed `SaMasterFingerprint`) — `SaPaymentTerm`, `SaSalesRep`, `SaTaxGroup`, `IvAreaCode`, `SaCountry`, `SaCurrency`, `SaCurrRate`, `SaDisGroup`. `SaCurrRate` pairs Level B with `UPDLOCK/HOLDLOCK` on the parent; `SaLMW` uses a `Serializable` transaction for its window-overlap rule.
**Why it matters.** The plan says "tenant/audit plumbing" and stops. Left to the implementer this family will mix levels (header Level A, lines Level B), and the save/delete key shape, the concurrency error path and the test seeding all differ per level. Also, Level A on SQLite forces `RowVersion` to `ValueGenerated.Never`, so service-inserted rows come back with an empty token and `NormalizeCompanyTokens` drops them — tests must seed explicit tokens.
**Recommended change.** State the level **per new entity** in the schema/decision section, using the v1 rule of thumb: Level A for every new/alterable master; Level B only where the table cannot carry one or the aggregate saves as a unit. If `SaDisGroupItem` keeps Level A, its qty-band/date overlap check must still run inside a transaction (the `SaLMW` `Serializable` precedent). Add the SQLite token-seeding note to the test matrix.
**Where.** Decisions; Phase 1; test matrix; Verification step 2.

### R-8 🟠 HIGH — DDL create-vs-alter branch, and the "`IF OBJECT_ID IS NULL` never converges" trap

**Problem** *(verified)*. `scripts/` are additive, idempotent and DBA-run. The two column patterns in use are `IF OBJECT_ID(...) IS NULL CREATE TABLE …` and `IF COL_LENGTH(...) IS NULL ALTER TABLE … ADD …` (`scripts/alter-sacust-profile.sql:55`). Known repo behaviour: an `IF OBJECT_ID IS NULL` guard **never converges an existing table**, so script-vs-live drift is normal here (`SaCurrRate.Status` is `bit` while the script says otherwise; `nvarchar(40)` vs the script's 20). Separate constraint: the SQL Server test DB is bootstrapped by EF `EnsureCreatedAsync`, so `init-*.sql` cannot bootstrap a standalone DB from scratch.
**Why it matters.** Phase 1's DDL is the one irreversible artifact. If the item-family tables exist live (likely — the legacy app writes them), a `CREATE TABLE`-only script silently skips them and the EF model then disagrees with reality (lengths, nullability, missing tenant columns) → truncation errors and phantom concurrency failures.
**Recommended change.** Phase 0 returns, **per table**, EXISTS/ABSENT **plus the live column matrix** (type/length/nullable/identity) and the live PK/index list. Phase 1 then ships one script with both branches (create when absent; add missing columns only, never re-type an existing one) and Verification step 4 becomes a schema **diff against the matrix**, not merely "run it twice".
**Where.** Phase 0; Phase 1; Verification step 4.

### R-9 🟠 HIGH — "seed `UnitPrice` from the item's selling price" is not implementable with today's lookup row

**Problem** *(verified)*. `IvStockMasterPicker` hands back `IvStockMasterLookupRow` = `ICode, IDesc, IType, IClassCode, StdUom, DefWarehouse, DefLocation, LotControl, PurchasePrice`. It carries **no `SellingPrice` and no `SellingUom`** (`IvInventoryLookupService.cs:16-29`). The plan's replacement default for defect #3 therefore cannot be honoured from the picker payload, and the path of least resistance is to leave the price at 0 — which is precisely the legacy bug being replaced (`CustProfItem.Save` reading `IvMas` by *customer* code and falling back to 0).
**Why it matters.** An unimplementable stated rule becomes a silent 0, and the defect returns wearing a plan badge.
**Recommended change.** Make the plumbing explicit: add `SellingPrice`/`SellingUom` to `IvStockMasterLookupRow` (additive; the row is a lookup DTO, so the seven inventory screens are unaffected) **or** fetch the item by key in the sales service. State which, and add a test asserting the seeded price equals the item's selling price and that a deliberate 0 is still allowed.
**Where.** Defect #3 replacement text; Phase 2/4; test matrix.

### R-10 🟡 MEDIUM — reuse the *lookup* service, not the gated master service, for item/UOM/class

**Problem** *(verified)*. There is no `IvUom` type: the master is `MsUom` (table `MsUOM`, `DbSet<MsUom>`), served by `IvUomList.razor` at `/inventory/uoms` under menu `INV_UOM`, and its service `IvInventoryRefService` gates every call with `RequireCompanyScopeAsync(MenuCodes.InventoryUom, PermissionCodes.Access)`. By contrast `IIvInventoryLookupService` is **ungated** and already exposes `SearchStockMastersAsync`, `ListActiveUomsAsync`, `ListActiveClassesAsync`, `ListActiveTypesAsync`, `ListActiveSubClassesAsync`.
**Why it matters.** Phase A step 8 as written ("tie `IvCustPrice.UOM` and `SaDisGroupItem.IClass` to the existing `IvClass` (`/inventory/classes`) and `IvUom` (`/inventory/uoms`) masters") points at a non-existent type and at *pages* rather than services. Worse, if an implementer reaches for `IvInventoryRefService`, a sales-only user without `INV_UOM`/`INV_CLASS` access cannot manage the new masters at all — a permission leak that only shows up in negative smoke testing.
**Recommended change.** Name the ungated lookup in the plan: `IIvInventoryLookupService.ListActiveUomsAsync` / `ListActiveClassesAsync` via the existing `IvCodeComboBox`. Add the rule "UOM/class values are validated against the active `MsUom`/`IvClass` rows for the caller's company; they are **not** constrained to the item's `SellingUom`" — legacy deliberately allows the same item with different UOMs and prices (the control comment records this), so item-limited validation would break a real feature.
**Where.** Phase A step 8; Phases 3/4/5.

### R-11 🟡 MEDIUM — "audit plumbing" is undefined, and no field-level audit infrastructure exists

**Problem** *(verified)*. Blazor has no audit-trail service — only `IvTrxHistory` (inventory) and Serilog. The master convention is the legacy column quartet (`Created`/`UserID`/`Updated`/`UpdatedUID` → `CreatedDate/CreatedBy/ModifiedDate/ModifiedBy`). Legacy item-family code had `SalesMasterHelper.AuditLog*`, which is legacy-only and has no Blazor counterpart.
**Why it matters.** "Audit plumbing" reads as "there is something to reuse"; there is not. Unresolved, someone will half-build one, and the legacy defect "`Created` is never persisted by any of the four adapters" will be reproduced verbatim.
**Recommended change.** Replace the phrase with the concrete contract: the four columns, the stamping rule per operation, and the explicit statement that field-level old/new audit is **out of scope** (mirroring v1).
**Where.** Shared contract; Phase 1.

### R-12 🟡 MEDIUM — delete lifecycle: bring the v1 delete matrix, and use `DeleteCheckResult.Ok(message)` for the deferred consumers

**Problem** *(verified)*. v1 shipped a per-master matrix ("Referenced by / DB FK / App check / Block | Allow if no refs") in `ErpWeb/docs/sales-master-phase0-findings.md`, and `DeleteCheckResult.Ok(string message)` exists specifically so "no consumer yet" is an explicit note rather than a silent `true`.
**Why it matters.** Three of the four new masters have no consumer today, which makes "delete freely" tempting and wrong: `IvCustPriceGroup` becomes referenced the moment D2-23 wires `SaCust.CustPriceCode`, and `IvCustPrice`/`SaItemCust`/`SaDisGroupItem` are referenced the moment the resolution contract lands.
**Recommended change.** Ship the same matrix in the item-family Phase 0 findings, and decide the header/line rule explicitly (header delete blocked while lines exist, plus line delete allowed — or cascade with a stated reason). Use `Ok(message)` for the "consumer deferred" answers.
**Where.** Phase 0 findings; shared contract; Phases 2/3/5; test matrix.

### R-13 🟡 MEDIUM — `VIEW_PRICE` needs three touch points, not one constant

**Problem** *(verified)*. `PermissionCodes.All` is consumed by `AccessRightService` (admin bypass) and `PermissionAdminService:211` (guards "Built-in permissions cannot be deleted"), and the built-in rows are seeded by `scripts/init-menu-access.sql` (`VIEW_COST`/`VIEW_PROFIT` as `PermissionType='Data'`, SortOrder 19/20, MERGE-idempotent). `VIEW_COST` itself is only read via `CanAsync("VIEW_COST")` and used for visibility; the server still persists real prices (`PoPrServiceTests.cs:686` is the shipped proof).
**Why it matters.** D2-22 says "as a constant in the `ViewCost` style" — correct but incomplete: a constant with no `All` entry is admin-deletable and behaves unlike every other built-in, and with no seed row every company must invent the permission by hand, so the feature ships dark.
**Recommended change.** D2-22 names all three: constant + `PermissionCodes.All` entry; seed row in the menu SQL (`PermissionType='Data'`); service-side `CanAsync` read with the rule *visibility only — the server never trusts a client price and never zeroes a persisted one*, plus a test mirroring `PoPrServiceTests:686` (deny → flag false → value still persisted).
**Where.** D2-22; Phases 2/3; Phase 7 script; test matrix.

### R-14 🟡 MEDIUM — the D-6 validation contract must be quoted, not paraphrased

**Problem** *(verified)*. `ISaCustLookupService.ValidateSubGroupAssignmentAsync`'s doc comment defines the contract in three clauses: blank is allowed; a non-blank value must exist in the caller's company; **the value already on the row is tolerated** so legacy free text cannot block an unrelated edit. D2-23 cites "v1 D-6 parity" without restating it, and refers to the shipped methods as `ListXForAssignmentAsync`/`ValidateXAssignmentAsync`, whereas the real names follow `List<Thing>sForAssignmentAsync` / `Validate<Thing>AssignmentAsync` (e.g. `ListDisGroupsForAssignmentAsync`, `ValidatePayCodeAssignmentAsync`).
**Why it matters.** The third clause is the difference between a migration-safe conversion and "every customer carrying a retired price-group code becomes uneditable" — the exact regression D-6 was written to prevent. Wrong method names cost a round trip in an implementation session.
**Recommended change.** Quote the three clauses in D2-23, and write the real names: `ListPriceGroupsForAssignmentAsync` + `ValidateCustPriceCodeAssignmentAsync` (plus the empty-list rule: legacy-adjacent fields keep the empty-list bypass, new masters fail closed — the v1 convention).
**Where.** D2-23; Phase 2; Phase A step 8.

### R-15 🔵 LOW — test strategy specifics that decide whether "green" means anything

*(verified)* SQL Server concurrency tests self-skip unless `ConnectionStrings:SqlServerTestConnection` points at a DB whose name contains "test"; `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` promotes that skip to a failure (v1 used it for TC29); `InventoryTenantContext.MaxCompanyLength = 5`, so test company codes must be ≤5 chars; Level A needs explicit tokens on SQLite. Fold all four into the test matrix so a green run cannot mean "nothing executed".
**Where.** Test matrix; Verification step 2.

### R-16 🔵 LOW — `PermissionCodes.InternalAdjustment` is absent from `PermissionCodes.All`

*(verified, out of scope)* — so it is admin-deletable and excluded from the admin bypass list. Either it is deliberate (then a comment should say so, or it will keep looking like the R-13 trap) or it is a real gap. Do **not** bundle the fix into this plan; note it and move on.

### 🟢 GOOD — keep exactly as-is

- Reusing the shipped item lookup/CRUD instead of inventing a sales item master, and **deleting** D2-18 rather than maintaining a second item story.
- `VIEW_PRICE` as a visibility-only permission following the shipped `VIEW_COST` precedent (server keeps persisting real values).
- Wiring `SaCust.CustPriceCode` (D2-23) instead of leaving a dead free-text box — this is the only cross-module link the family has.
- Reuse of the export pair (`SaMasterRefExportWorkbooks.cs`, `SaMasterRefExportEndpoints.cs`, `SaRefListPageBase.ExportRoute`) and of `IvMasterOperationResult<T>`/`IvMasterErrorCode`.
- The menu trap is treated as a first-class deliverable (XML + `scripts/init-*-menu.sql` + `MenuDeploymentParityTests` guard) — repo memory confirms this has cost a round trip before.
- Phase 0 as a **binary** PASS/FAIL gate placed before any code, with FAIL defined as "stop and revise the plan".
- Correction-first-then-code sequencing, and refusal to port the legacy shared session key (`"SACUSTITEMSSALES"`), the display-only "another user's item" checks, and the `IvMas`-by-customer-code price default.

## Fold-in checklist (what to change, where)

| # | Change | Target |
|---|---|---|
| 1 | Give the v2 document its own path and materialise it; re-point step 1 + Relevant files | Phase A step 1; Relevant files |
| 2 | Item authority = invariant + named writers + `IvMas` disposition | Phase 0 questions; Decisions; Phase 1 |
| 3 | New section: Price & discount resolution contract (key, precedence, indexes, tokens, tie-break, NULL rule, slot mapping) | Shared contract; D2-24 |
| 4 | New Phase 0 facts: item-discount query of record; live 0/0 rows; live column matrix + PK/index per table; external writers per table; does any consumer need branch-level prices | Phase 0 |
| 5 | Close the 0/0 qty band as a validation decision | Phase 0; Phase 5; test matrix |
| 6 | `SaDisGroup`/`SaDisCust` single-writer invariant; `SaDisGroupItem.GroupName` disposition | Decisions; Phase 6; Verification 6 |
| 7 | D2-21 → surrogate-key-first (option a) with the framework change as fallback; state the tenant rule (Company in key; Branch/Location as leftover stamps unless a consumer needs otherwise) | D2-21; Phase 1 |
| 8 | Concurrency level per new entity (Level A default; Level B only with a reason; aggregate overlap check inside a transaction) | Decisions; Phase 1; test matrix |
| 9 | DDL branch (create vs add-column) + schema-diff verification | Phase 1; Verification 4 |
| 10 | Make the defect-#3 price default implementable (`SellingPrice`/`SellingUom` on the lookup row or an explicit fetch) | Defect #3; Phase 2/4; test matrix |
| 11 | Name `MsUom`/`IvClass` + `IIvInventoryLookupService` (ungated) in the UOM/class tie-in; add the "not item-limited" rule | Phase A step 8; Phases 3/4/5 |
| 12 | Replace "audit plumbing" with the four-column contract + "field-level audit out of scope" | Shared contract; Phase 1 |
| 13 | Delete matrix + `DeleteCheckResult.Ok(message)` usage | Phase 0 findings; Phases 2/3/5 |
| 14 | `VIEW_PRICE` = constant + `All` + seed row + service read + deny test | D2-22; Phases 2/3/7; test matrix |
| 15 | Quote the D-6 three-clause contract; fix the lookup method names | D2-23; Phase 2; Phase A step 8 |
| 16 | Test-matrix preconditions (scratch DB naming, `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`, ≤5-char company codes, explicit Level-A tokens) | Test matrix; Verification 2 |

---

# Review v2 — best-practice feedback folded in (2026-09-15)

Second review pass, folded in as plan content. Where it overlaps the first pass it **supersedes** it; where it only sharpens it, it **amends** it. Everything below is re-verified against this repo; nothing is copied in unverified. The reviewer's target structure (§25 of the input) and decision register (§24) are adopted as the skeleton and the gate.

## How Review v2 relates to Review v1

| Review v2 item | Effect on Review v1 |
|---|---|
| §2 domain model | **Amends R-1/R-2** — the v2 document must open with a domain model, not a table list |
| §3 price resolution, §4 discount resolution | **Supersedes R-3** — R-3 named the gap; these two become named contracts with content |
| §5 effective dates | **Supersedes R-4** in part — and is *scoped* by N-3 below: legacy price tables have **no** date columns at all |
| §6 quantity bands | **Extends R-4** — adds `QtyFr > 0`, `QtyTo >= QtyFr`, inclusivity, numeric type |
| §8 tenant matrix | **Sharpens R-7/R-12** — the matrix is the set of new `InventoryLeftoverSite.Apply` overloads |
| §9 duplicate prevention | **Sharpens R-6** — adds the DB-constraint + race-test requirement |
| §10 conservative surrogate keys | **Amends R-6** — see N-6; the natural key is preserved as the business key first |
| §11 transaction contract | **Extends R-12** — header/line atomicity becomes a stated service contract |
| §12/§13 delete + lifecycle | **Extends R-12** — adds Active/Inactive/Deleted as three distinct states |
| §14 `VIEW_PRICE` projection | **Amends R-13** — see N-8: projection-level masking is a *deviation* from the shipped `VIEW_COST` precedent, and must be declared as such |
| §16/§17 lookup + indexes | **New** — see N-7, N-11 |
| §18 rounding, §19 currency, §20 tax | **New** — see N-1, N-2, N-9 |
| §21, §22 phase sequence / no second `SaDisGroup` | **Confirms** Review v1's 🟢 list |

## New findings (N-series)

### N-1 🔴 BLOCKER — the price *basis* (tax-inclusive vs exclusive) is undefined, and the shipped engine un-taxes what it is given

**Problem** *(verified)*. `SaInvoiceCalc` documents the line semantics as "the entered `UnitPrice` *includes* tax (legacy semantics)" and un-taxes it (`exclusiveUnitPrice = UnitPrice / (1 + t)`) on the inclusive path; the exclusive path also exists (`SalesCalcMatrixTests` carries `exc_*` and `inc_*` cases plus a `decPoint` axis). Nothing in the plan says whether `IvStockMaster.SellingPrice`, `IvCustPrice.SellingPrice` and `SaItemCust.UnitPrice` are tax-inclusive or tax-exclusive amounts.
**Why it matters.** For a Malaysian ERP this is a correctness bug generator: seeding a tax-*exclusive* master price into an inclusive line inflates the net by the SST rate, and the error only surfaces in totals. Tax itself is out of scope (correct), but the *basis of the stored price* cannot be.
**Recommended change.** Add one explicit sentence to the price contract: the basis of each stored price field, plus the conversion performed at the boundary if the line's basis differs. Do not introduce tax logic into the master.
**Where.** Price resolution contract (§7 of the target structure); Phases 2/3/4.

### N-2 🔴 BLOCKER — currency: price groups have none, customer items do; the plan must say what that means

**Problem** *(verified — `docs/sales-item-family-logic.md` §1)*. `IvCustPrice` has **no currency column** (its columns are `CustPriceCode, CustPriceDesc, ICode, SellingPrice, SellPackSize, UOM, IDesc, audit ×4, tenant ×3`). `SaItemCust` **does** (`Currency nvarchar(5)`). `SaCust.Currency` and `SaCurrRate` exist and currency resolution is already fail-closed elsewhere (`SaCdnService.ResolveCurrRateAsync`).
**Why it matters.** "A price group is company/base-currency only" is a real business constraint that leaks into every future SO/Customer lookup. Left unsaid, someone adds a currency column "for completeness" (schema drift) or assumes price groups are multi-currency (wrong prices).
**Recommended change.** State it: price-group prices are base/company currency (no column, do not add one without a Phase 0 finding); `SaItemCust.UnitPrice` is accompanied by `Currency`; and define what the consumer does when the customer's currency differs — fail closed, or convert via `SaCurrRate`. No silent new column.
**Where.** Domain model; price contract; Phase 0 question bank.

### N-3 🟠 HIGH — effective dating exists on exactly one of the four tables; scope the standard accordingly

**Problem** *(verified — `docs/sales-item-family-logic.md` §1)*. `IvCustPrice` and `SaItemCust` have **no date columns at all**; only `SaDisGroupItem` carries `DateFr`/`DateTo` (`datetime`). The v2 review's §5 "standardize effective dates" therefore applies to *one* table, and implicitly proposes adding dating to the others.
**Why it matters.** Adding `EffectiveFrom/To` to price tables with no consumer is speculative schema, and the plan cannot answer "what wins on overlap" for tables that currently have no date dimension. Conversely, if price history *is* needed, the right time to say so is now, not in a second migration.
**Recommended change.** Split the decision: (a) `SaDisGroupItem` — standardize on `date` (not `datetime`), inclusive bounds, `EffectiveFrom` required / `EffectiveTo` nullable-or-required (choose), overlaps **rejected in the service inside the transaction** unless a priority column is added (legacy has none); (b) price tables — declare *undated* with the consequence stated (re-pricing edits a row in place; there is no price history; only audit columns record who/when). If dating is wanted for prices, it is a deliberate additive decision with a named consumer.
**Where.** Shared contract; Phase 0; Phases 3/4/5.

### N-4 🟠 HIGH — quantity numeric types are inconsistent across the legacy family and must be unified in the new model

**Problem** *(verified)*. `SaItemCust.MOQ` is `int`; `SaDisGroupItem.QtyFr/QtyTo` are `float`; line quantities are `decimal(18,4)`; `IvStockMaster.MinStock/MaxStock/StdPackSize` are `decimal(18,4)`; `IvCustPrice.SellingPrice` is `decimal(18,4)` while `SaItemCust.UnitPrice` is `float`.
**Why it matters.** `MOQ` is a **price selector** (legacy picks the highest MOQ ≤ order qty), so an `int` MOQ cannot express fractional purchase units, and `float` money/quantity produces exact-equality failures in band matching (`QtyFr <= qty` on floats).
**Recommended change.** One numeric policy in the schema matrix: quantities and band bounds `decimal(18,4)`; money `decimal(18,4)`; never `float`/`real` for either. Where a live legacy column is `float`, the additive script must **not** silently re-type it — record the divergence, and convert in the service with an explicit scale.
**Where.** Schema matrix; Phases 1/4/5.

### N-5 🟠 HIGH — `SaItemCust.Status` is a *refresh* flag, not an active/inactive flag

**Problem** *(verified — logic spec §1.1/§4.1)*. `SaItemCust.Status` takes `NEW` on save and `FALSE`, and `FALSE` drives the legacy "refresh" flow (`ItembyCustViewEx` → `SalesMasterHelper.RefreshCustomerProduct`: select `Status='FALSE'` rows for the tenant, set `Status='NEW'`, update in a transaction). By contrast `SaDisGroup.GroupStatus='NEW'` **is** an activation flag — consumer queries filter `GroupStatus='NEW'` — and `SaDisGroupItem.GroupStatus` is written `NEW` on add/save.
**Why it matters.** If the port collapses `Status`/`GroupStatus` into one `IsActive` toggle, the refresh workflow breaks (or every "refresh" click deactivates a customer item). This is exactly the kind of semantic collision that survives review because the column names look alike.
**Recommended change.** Per-table lifecycle decision, naming the semantics: `SaItemCust.Status` = refresh state (keep both values, expose no Active toggle); `SaDisGroupItem.GroupStatus`/`SaDisGroup`-equivalent = activation/validity; and state whether inactive rows remain usable by historical documents (recommended: yes — historical integrity) while new transactions cannot select them.
**Where.** Domain model; lifecycle contract; Phases 4/5.

### N-6 🟠 HIGH — key policy: preserve the natural key first, surrogate only when the framework forces it

**Problem.** Review v1's R-6 recommended the surrogate-key route first; Review v2 §10 pushes back, correctly, on adding technical IDs merely to make Blazor convenient.
**Synthesis (recommended).** Per table, in this order: **(1)** the natural/business key stays authoritative *in the database* (PK where it already is, otherwise a unique index) — it is what users see and what imports/exports and the future SO/INV contract must use; **(2)** the page key is the existing surrogate when one exists (`SaDisGroupItem.ID`, IDENTITY — reuse it, do not invent a second); **(3)** the natural key may serve as the page key only when it fits `Code` + `ParentCode`; **(4)** add a surrogate **only** as a last resort, and never retrofit a new PK onto a live populated table (destructive). If a surrogate is added, the business key keeps a unique constraint.
**Why it matters.** This keeps D2-21 from turning into a framework change *and* keeps the ERP's identity semantics intact for the future consumer.
**Where.** D2-21 (rewrite to this order); Phase 1; Phase 3/5.

### N-7 🟠 HIGH — lookup shape: the `ListXForAssignmentAsync` convention is safe for code tables, not for these

**Problem** *(verified)*. `ISaCustLookupService.List<Thing>ForAssignmentAsync` returns whole tables (`IReadOnlyList<IvCodeLookupRow>`) — fine for `SaCustType`/`SaCustGroup`-sized masters. `IvCustPrice` is per price-group × item × UOM, `SaItemCust` per customer × item × UOM × MOQ, `SaDisGroupItem` per item × band × window — all potentially large. The item side already shows the split: `IvStockMasterRepository.ListActiveForLookupAsync(company)` (unbounded) vs `SearchActiveAsync(...)` (paged; `IvStockMasterSearchRequest` with `Page`/`PageSize = 200`), plus a sort allow-list (`IvStockMasterSortFields.Allowed`).
**Why it matters.** Blazor Server multiplies this: a few dozen circuits each pulling a full table is how a picker becomes an outage.
**Recommended change.** State the shape per new lookup: paged + search-text + max-rows + active filter + company filter + sort allow-list (copy `IvStockMasterSearchRequest`/`IvStockMasterSortFields`). Explicitly forbid an unbounded assignment list for the three large tables. If a customer-scoped picker needs "all rows for one customer", that is a bounded query by design and should say so.
**Where.** Service contract; Phase 2/3/4/5; test matrix (one paging test).

### N-8 🟡 MEDIUM — projection-level price masking is a *deviation* from shipped precedent; declare it or don't do it

**Problem** *(verified)*. The shipped `VIEW_COST` model is: service publishes `CanViewCost` in the lookup/DTO, the **UI** hides columns (`PoPr.razor`/`PoOrder.razor` `@if (CanViewCost)`), and real values are still persisted (the deny test proves persistence, not masking). The v2 review asks for masking at the service/projection boundary.
**Why it matters.** Both are defensible, but they are different contracts. If the plan silently adopts masking, the new code diverges from `VIEW_COST` and from the "never alter persisted values" rule; if it silently adopts hide-in-UI, the reviewer's requirement is unmet.
**Recommended change.** Choose explicitly. Recommended: keep persistence identical to `VIEW_COST` (never trust or zero a client value), **plus** one added rule for this feature — a read path that exists only to render a price column must return the value only when authorized (no "return then hide"). State the threat model honestly: in Blazor Server the component runs server-side, but any rendered value crosses the circuit.
**Where.** Security section; Phases 2/3; test matrix (deny test asserts both flag and payload).

### N-9 🟡 MEDIUM — rounding points must be named once, and the masters must not round

**Problem** *(verified)*. Money rounding lives in the document engine: `SaInvoiceCalc.Money(value, 2, AwayFromZero)`, `SaCdnCalc.MoneyNormalize(amount, decPoint) => Money(amount, decPoint ? 0 : 2)` driven by `SaCust.DecPoint` (0 dp for whole-currency customers), with a live defect precedent where `decPoint` was hard-coded `false` in one path (`docs/sales_trans_enhancement.md` R6). The matrix tests pin 0 dp and 2 dp behaviour.
**Why it matters.** A second rounding implementation inside a master service will disagree with the document engine at the 0 dp/2 dp boundary — the exact class of bug the repo already has.
**Recommended change.** One sentence in the contract: masters store 4 dp and **never** round; rounding happens only at the named document points (`Money(...,2)` / `Money(...,0)` when `SaCust.DecPoint`), and any master-side display rounding is presentation-only.
**Where.** Price/discount contracts; Phases 2/3/4/5.

### N-10 🟡 MEDIUM — duplicate prevention: the idiom already ships, use it (three layers)

**Problem** *(verified)*. Services catch unique violations today: `IvStockMasterService.cs:422` and `SaCustService.cs:225` (`when (IsDuplicateKey(ex))`), `PoSupplierService.cs:242`, and the transaction services use `IsUniqueViolation(ex)` (`PoInvoiceService:381`, `SaInvoiceService:572`, `SaSoService:537`, …); `DocumentNumberingService.cs:504` inspects `SqlException.Number is 2601 or 2627`; the SQL Server test template exists (`SaCustSqlServerConcurrencyTests.SqlServer_DuplicateKey_ReturnsDuplicateKey`). Note the same predicate appears under two different private names.
**Why it matters.** The v2 review's "unique index + service check + race test" is exactly right, and the repo already has all three pieces — so the plan should *name* them rather than let an implementer invent a fourth variant.
**Recommended change.** Require per table: a DB unique index on the business key; a service-level pre-check returning `IvMasterErrorCode.DuplicateKey` (create is never a silent upsert — v1 D2); and a SQL Server concurrency test copying `SqlServer_DuplicateKey_ReturnsDuplicateKey`. Reuse the existing predicate; if a third copy is needed, extract one shared helper (optional cleanup).
**Where.** Duplicate/business-key decision; Phases 2/3/4/5; test matrix.

### N-11 🟡 MEDIUM — index plan derives from the resolution contract, not from PKs

**Problem.** The plan states keys but not indexes. The repo has precedent: `IvStockMasterConfiguration` indexes `(CompanyCode, IType)`, `(CompanyCode, IClassCode)` and `IsActive`; `SaCurrRate` carries `IX_SaCurrRate_CurrCode_Dates`; `PoPurItem` indexes `(CompanyCode, ICode, Vendor)`.
**Recommended change.** Add an index section listing the query shapes the contracts imply — `(CompanyCode, CustPriceCode)`, `(CompanyCode, CustPriceCode, ICode, UOM)` (unique), `(CompanyCode, CustCode, ICode)` for `SaItemCust`, `(CompanyCode, ICode, DateFr, DateTo)` for `SaDisGroupItem`, plus the active-flag index — with the rule that every index must be traceable to a contract step, and none to speculative use.
**Where.** DDL/migration section; Phase 1; Verification 4 (schema diff includes indexes).

### N-12 🟡 MEDIUM — loader/tenant plumbing: the four new `InventoryLeftoverSite.Apply` overloads *are* the scope matrix

**Problem** *(verified)*. `InventoryLeftoverSite` (in `InventoryTenantContext.cs`) already has one `Apply` overload per entity — including `SaCust`, `SaDisGroup`, `IvStockMaster`, `MsUom`, `IvClass` — and none for the four new tables. `MaxCompanyLength = 5`.
**Recommended change.** Express the tenant matrix concretely as "what each new overload does", so the decision is executable rather than a table of question marks: normalize Company; leftover-stamp Branch/Location (the shipped convention for masters without branch semantics); never invent a fallback hierarchy — the repo has none, so state "no fallback: a record is visible only in its company" explicitly, and if branch-level pricing is wanted it must be a Phase 0 finding with a consumer.
**Where.** Tenant matrix; Phase 1; test matrix (2-company isolation test per table).

## Contracts the v2 document must now contain

These are **content requirements** — the plan must answer them, not reference them. Evidence pointers are given so the answers are grounded.

**C-1 Price resolution contract.** Input key `(CompanyCode, BranchCode?, CustCode, ICode, UOM, Qty, DocDate, PayCode)`; candidate sources in declared order; per-source filters (active flag, currency, UOM equality, MOQ/quantity band, date window); specificity rule (customer-specific vs group vs item default); tie-break; fallback; zero/negative handling; the tax basis (N-1) and rounding point (N-9); and "no match" behaviour. Legacy evidence to confirm rather than assume: `so_logic_spec` — "normal = customer/item UOM price; Mode 3 = highest MOQ ≤ order qty, else block Add"; `SaCustomerBL`/`SalesDicountHelper` item-discount queries; `SaCust.PriceMethod` (`FOLLOW SELLING PRICE X DISCOUNT` / `FOLLOW DEFAULT DEALER PRICE`); `SaDisGroupItem.EffectPrice` (`DEALER`|`SELLING`).

**C-2 Discount resolution contract.** Pipeline: base price → effect-price selection → rule selection → slot 1 → slot 2 → … → final unit price. Must answer: percentage compounding vs stacking; amount before/after percentage; what `JOIN` and `SPLIT` mean (verified: the shipped helper implements JOIN as "replace the percent stack with the sum against the original price" and has **no SPLIT branch**, `CustDiscount` unused); are `Discount`/`Discount1` sequential; does zero mean "no discount"; are negatives or >100% allowed; can amount discounts exceed the base; rounding point and precision (N-9); and the exact mapping of the legacy two slots onto the shipped line model's percent slots vs amount slots.

**C-3 Price basis, currency, tax statement.** Covers N-1 and N-2, plus the explicit sentence "Sales Master stores commercial price/discount rules only; SST/GST/tax determination stays in the existing tax engine."

**C-4 Effective-date and quantity-band standard.** Covers N-3, N-4 and Review v1's R-4: `QtyFr > 0`, `QtyTo >= QtyFr`, inclusive bounds, match rule `QtyFr <= qty AND QtyTo >= qty`, decimal types, and the 0/0 prohibition (or its documented meaning).

**C-5 Tenant scope matrix.** Company / Branch / Site / Global per table, expressed as the `Apply` overloads (N-12), plus "no fallback" stated.

**C-6 Key and duplicate policy.** N-6 ordering + N-10 three layers + the business key per table.

**C-7 Aggregate transaction contract.** Header + lines in one transaction; validation order (header → lines → duplicates → UOM → item → dates → key conflicts); any line failure rolls back everything; no half-saved group. Reuse precedents: `SaSalesRefService.FlatRef.SaveFlatMasterAsync` (one shared save for five masters), `SaDisGroup` membership sync in one transaction, and `SaLMW`'s `Serializable` transaction for a window-overlap rule (the pattern for the band/date check).

**C-8 Lifecycle: Active / Inactive / Deleted.** Covers N-5 and the review's §13: which column means what per table, whether inactive rows stay valid for historical documents, whether an inactive group can remain assigned to a customer, and "never hard-delete historical pricing" with the reference policy that backs it.

**C-9 Delete semantics.** Parent/child, per the review's §12: header blocked while lines exist; header blocked while a customer references it (`SaSalesRefService.cs:3184` is the shipped precedent for `SaDisGroup`); line delete permitted; **no database cascade** for business master data unless deliberately approved; use `DeleteCheckResult` and its `Ok(message)` overload for deferred consumers.

**C-10 Security (`VIEW_PRICE`).** Constant + `PermissionCodes.All` + seed row + `CanAsync` read + deny test (Review v1 R-13), **plus** the projection-boundary decision in N-8.

**C-11 Lookup/list performance.** N-7, with the paged request shape named.

**C-12 Index plan.** N-11.

## Decision register — close these before Phase 1

| Decision | Status | Where the answer lands |
|---|---|---|
| Item-master authority (`IvMas` disposition) | 🔴 must close | Phase 0 findings |
| Domain model (role of each of the 7 objects) | 🔴 must close | v2 doc §4 |
| Price precedence | 🔴 must close | C-1 |
| Discount precedence | 🔴 must close | C-2 |
| Price basis (tax-inclusive/exclusive) | 🔴 must close | C-3 |
| Currency policy | 🔴 must close | C-3 |
| Effective-date rule (scoped per N-3) | 🔴 must close | C-4 |
| Quantity-band semantics (incl. 0/0) | 🔴 must close | C-4 |
| Company/branch/site scope + fallback | 🔴 must close | C-5 |
| Business keys + duplicate prevention | 🔴 must close | C-6 |
| Concurrency level per entity | 🟠 close | decisions; Phase 1 |
| DDL create vs add-column strategy | 🟠 close | Phase 1 |
| Natural vs surrogate key | 🟠 close | D2-21 rewrite |
| Delete lifecycle | 🟠 close | C-9 |
| Active/Inactive/Deleted | 🟠 close | C-8 |
| `VIEW_PRICE` implementation + projection | 🟠 close | C-10 |
| D-6 validation contract quote | 🟠 close | D2-23 |
| Audit columns contract | 🟠 close | shared contract |
| Rounding points | 🟠 close | C-1/C-2 |
| UOM conversion scope | 🟡 document | C-3/C-4 |
| Tax scope | 🟡 document | C-3 |
| SO/Invoice consumer | 🟡 contract now, implement later | C-1/C-2 |

## Target structure for the (materialised) v2 document

Adopted from Review v2 §25, with Review v1's items folded in. This is the skeleton for `plans/sales-item-family-v2-plan.md` (Review v1, R-1):

1 Objective · 2 Scope / non-scope · 3 Existing-system findings · 4 Domain model · 5 Data ownership & authority · 6 Tenant / company / branch scope · 7 Price resolution contract · 8 Discount resolution contract · 9 Data validation rules · 10 CRUD / lifecycle rules · 11 Permission & security model · 12 Concurrency model · 13 DDL / migration strategy · 14 Blazor UI architecture · 15 Service / repository architecture · 16 Phase 0 gate (binary) · 17 Phase 1 infrastructure · 18 Phase 2 price group · 19 Phase 3 customer price · 20 Phase 4 customer item · 21 Phase 5 discount item · 22 Phase 6 existing `SaDisGroup` reconciliation · 23 Phase 7 menu / permission / export / documentation · 24 Test matrix · 25 Deployment / rollback · 26 Deferred SO/Invoice integration contract.

Section 4 is what keeps §7/§8 from silently treating all seven objects as equivalent masters; sections 7/8 are the two contracts above; section 26 is where "no consumer in v2" stops being an absence and becomes a written interface.

## Fold-in checklist (Review v2)

| # | Change | Target |
|---|---|---|
| 17 | Add the domain-model table (role of each of the 7 objects) before the phase list | v2 doc §4; Review v1 R-1 |
| 18 | Write C-1 (price resolution) and C-2 (discount resolution) as full contracts, with the legacy evidence cited and each open question answered | v2 doc §7/§8; supersedes Review v1 R-3 |
| 19 | Declare the tax basis of every stored price + the currency policy (N-1, N-2) | C-3; Phases 2/3/4 |
| 20 | Scope effective dating to `SaDisGroupItem`; declare price tables undated with the consequence (N-3); standardize on `date`, inclusive, no overlap | C-4; Phase 5 |
| 21 | One numeric policy: quantities/money `decimal(18,4)`, never `float`; record legacy divergences instead of re-typing live columns (N-4) | Schema matrix; Phase 1 |
| 22 | Per-table lifecycle naming, incl. `SaItemCust.Status` = refresh state, not Active (N-5) | C-8; Phase 4 |
| 23 | Rewrite D2-21 to the 4-step key order: natural key authoritative → existing surrogate → natural-in-`Code` → new surrogate last (N-6) | D2-21; Phase 1 |
| 24 | Paged/searchable lookup shape for the three large tables; forbid unbounded assignment lists (N-7) | C-11; Phases 2–5 |
| 25 | Decide projection masking vs shipped `VIEW_COST` precedent, and declare the choice (N-8) | C-10; Phase 3 |
| 26 | Name the rounding points; forbid rounding in master services (N-9) | C-1/C-2 |
| 27 | Three-layer duplicate prevention + the shipped `IsDuplicateKey`/`IsUniqueViolation` idiom + `SqlServer_DuplicateKey_ReturnsDuplicateKey` as the test template (N-10) | C-6; test matrix |
| 28 | Index plan traceable to contract steps (N-11) | C-12; Verification 4 |
| 29 | Tenant matrix expressed as the four new `Apply` overloads; state "no fallback" (N-12) | C-5; Phase 1 |
| 30 | Adopt the 26-section structure for the materialised v2 document | Review v1 R-1; v2 doc |
| 31 | Add the decision register to the plan (or to Phase 0's exit criteria) so "closed" is checkable | Phase 0 gate |

## Next gate

> **Do not start Phase 1 until the domain model, the price and discount resolution contracts, tenant scope, effective-date and quantity-band rules, price basis/currency, and the business-key/duplicate rules are explicitly answered in the plan.**

Review v2's scores (8/10 architecture, 6.5/10 business-rule completeness, ~7.5/10 overall) are consistent with this file's own verdict: the remaining work is **business contract**, not framework refactor. The single highest-value change is C-1/C-2 — deciding *who gets what price and what discount, from which source, for which item/UOM/quantity/date, at which precedence and rounding point*. Everything else — schema, keys, indexes, service methods, Blazor screens, and the deferred SO/Invoice consumer — follows from those two documents.

---

# Appendix A — Review v2 answer sheets (the content, not the pointer)

Coverage audit: §9–§14, §16–§22, §24–§26 are folded as findings N-1…N-12 / contracts C-1…C-12; §1, §21, §22 also appear in Review v1's 🟢 list. The items below were referenced but not yet *materialised*, so they are written out here. Where this repo already answers a question, the answer is marked **verified** and the remaining work is only the owner's choice; everything else is an owner decision.

## A.1 Domain model — the seven objects are not equivalent (§2)

| Object | Domain role | Data class | Blazor status |
|---|---|---|---|
| `IvCustPriceGroup` | price list / customer price-group header | **master** | to create |
| `IvCustPrice` | item price rule inside a price list (per item + UOM) | **rule data** (child of master) | to create |
| `SaItemCust` | customer-specific item override (customer part no, invoice desc, UOM, price, MOQ) | **assignment + rule hybrid** | to create |
| `SaDisGroup` | discount-group header (group + payment term) | **master** | shipped |
| `SaDisGroupItem` | item + quantity band + date window discount rule | **rule data** | to create |
| `SaDisCust` | customer → discount-group membership | **assignment** | shipped |
| `SaCust.CustPriceCode` | customer → price-list assignment | **assignment** (column on an existing master) | shipped, **unvalidated** |
| `SaCust.GroupDiscount` | customer → discount-group assignment | **assignment** | shipped, validated |

The distinction matters at implementation time: masters get CRUD + delete guards + activation; **rule** records get effective-date/quantity validation and overlap/duplicate keys; **assignment** records get referential validation only — and the customer-side assignments are exactly where D-6/D2-23 live. Section 4 of the v2 document must open with this table.

## A.2 Relationship map for price resolution (§3)

```
Customer (SaCust)
   |
   +-- CustPriceCode ------------------> IvCustPriceGroup      [assignment, D2-23]
   |                                        |
   |                                        +--> IvCustPrice  (ICode, UOM) -----> candidate price #2
   |
   +-- (customer + item) --------------> SaItemCust (ICode, CustCode, UOM, MOQ) -> candidate price #1
   |
   +-- GroupDiscount ------------------> SaDisGroup            [assignment, validated]
                                            |
                                            +--> SaDisGroupItem (ICode, qty band, window)
   |
   +-- item default -------------------> IvStockMaster.SellingPrice -------------> candidate price #3
   |
   +-- PriceMethod = "FOLLOW DEFAULT DEALER PRICE" / EffectPrice = DEALER -------> candidate price #4
                                                                                   (storage: UNKNOWN)
```

**Candidate price sources, to be answered in order** (the plan must state the precedence, not just list them):

| # | Candidate | Where it lives | Status |
|---|---|---|---|
| 1 | customer-item price | `SaItemCust.UnitPrice` | exists |
| 2 | price-list item price | `IvCustPrice.SellingPrice` via `SaCust.CustPriceCode` | exists |
| 3 | item selling price | `IvStockMaster.SellingPrice` | exists |
| 4 | "default dealer price" | **nothing found** in Blazor — `SaCust.PriceMethod` and `SaDisGroupItem.EffectPrice` both name a DEALER concept but no Blazor entity/column stores a dealer price | **Phase 0 must locate it or the token is inert** |
| 5 | no price | — | decide: 0, or block (legacy `so_logic_spec`: MOQ-mode misses block Add) |

Notes to stop the dealer-price search from going wrong: grepping `DEALER` in this repo today hits only (a) the `PriceMethod` token `SaCustPaymentOptions.PriceDealer`, (b) the `SaCustEntry` radio, and (c) `IvMSCode` `CodeType='CHANNEL'`, `Code='DEALER'` (a customer *channel* code, seeded by `scripts/init-sales-masters.sql` — **not** a price). Leads worth checking in Phase 0: the legacy item/price tables enumerated by the R-2 authority query (`IvMas`/`IvMasPack` price columns), the unexplained `SaItemCust.DG`/`SG` columns (already flagged Q5 in `docs/sales-item-family-logic.md`), and any legacy price-level table keyed by customer type/channel.

Also required by §3: UOM filter (equality — no conversion, A.6), minimum-quantity filter (band match, A.5), customer specificity (does #1 beat #2?), tie-break, and zero-price behaviour.

## A.3 Discount resolution pipeline and the ten questions (§4)

```
Base price (A.2 winner)
   -> effect-price selection   (EffectPrice DEALER|SELLING  vs  SaCust.PriceMethod)
   -> discount rule selection  (SaDisGroupItem match, A.5/A.4)
   -> slot 1 (Discount/DiscountType)
   -> slot 2 (Discount1/DiscountType1)
   -> final unit price
   -> rounding (N-9, engine only)
```

| Question | Evidence / recommended default |
|---|---|
| percentage compounding vs stacking | legacy active helper **stacks sequentially**; `Join` **replaces** the stack with `Σ% × original` (verified in `SaInvoiceCalc.CalculateDiscountPerUnit`, pinned by `SalesCalcMatrixTests`) |
| amount before/after percentage | shipped code: percentages apply to `UnitPrice`, then `ItemDiscAmount + ItemDiscAmount1` are added — keep that order |
| what `SPLIT` means | `SaCustPaymentOptions.DiscountSplit` exists but the shipped helper has **no SPLIT branch** (verified). Decide: reject SPLIT, or define it — do not leave a stored token that silently behaves like its opposite |
| are `Discount1/2/3` sequential | legacy writes only 2 slots; the shipped line has 6 percent + 2 amount slots → mapping is required, not optional |
| zero = no discount or explicit zero | a 0 slot is a no-op arithmetically; decide whether 0 rows are allowed to exist in `SaDisGroupItem` at all (A.5) |
| negative discounts | not observed in legacy; recommend **reject** (validation) |
| maximum percentage | not enforced in legacy; recommend reject > 100 |
| amount > base price | not enforced in legacy; recommend reject (or clamp + warn — decide, then test) |
| rounding point / precision | N-9: engine only, `Money(v, 2)` / 0 dp when `SaCust.DecPoint` |
| exact slot mapping | map `Discount/DiscountType` and `Discount1/DiscountType1` onto `CalculateDiscountPerUnit(unitPrice, itemDiscount, itemDiscount2..6, itemDiscAmount, itemDiscAmount1, discMethod)` explicitly, in the contract |

## A.4 Effective-date standard (§5)

Rule template to adopt (one place, reused everywhere):

```
EffectiveFrom <= DocDate  AND  (EffectiveTo IS NULL OR EffectiveTo >= DocDate)
```

| Question | Recommended default (to confirm) |
|---|---|
| Is `EffectiveFrom` mandatory? | yes |
| Is `EffectiveTo` optional? | yes — NULL = open-ended (legacy's tolerant query treats NULL as unbounded; the strict query excluded those rows — **pick the tolerant rule**, it is the one that can match live data) |
| Column type | `date`, not `datetime` (avoid time-of-day boundary misses; matches `SaLMW`'s `date` columns) |
| Inclusive bounds? | inclusive both ends |
| Same-day start/end valid? | yes |
| Overlaps allowed? | **no** — rejected in the service inside the transaction; legacy had no priority column, so an overlap is ambiguous pricing |
| Priority column? | not introduced (nothing in legacy needs it); if ever added it must come with a unique index including priority |
| "Current record overrides older"? | no such rule exists; do not invent one |
| Two rows, same priority | impossible while priority does not exist |
| Scope (N-3) | `SaDisGroupItem` only — `IvCustPrice`/`SaItemCust` have **no** date columns; declare price tables undated with the consequence stated |

## A.5 Quantity-band standard (§6)

| Rule | Value |
|---|---|
| `QtyFr` | `> 0` (0/0 remains forbidden unless a documented meaning is chosen — Review v1 R-4) |
| `QtyTo` | `>= QtyFr` |
| Types | `decimal(18,4)`, **non-nullable** for a band; no `float` (N-4) |
| Inclusivity | `QtyFr <= qty AND QtyTo >= qty` — stated once in the shared contract and reused by validator, lookup and tests |
| Max quantity | no artificial cap; the only constraint is `QtyTo >= QtyFr` |
| NULL handling | forbidden — a row with a NULL bound cannot be evaluated |
| MOQ fractional? | decide; `SaItemCust.MOQ` is `int` in legacy while bands are `float` — if fractional MOQ is wanted, MOQ becomes `decimal(18,4)` and the conversion is an explicit, documented change |
| UOM | the band is evaluated **in the document's UOM**; no conversion (A.6) |

## A.6 UOM and pack size (§7)

**Required statement in the document:** *"Sales Master stores a price against a specific UOM; automatic UOM conversion is outside this implementation scope."*

| Item | UOM | Price |
|---|---|---|
| ITEM001 | PCS | 10.00 |
| ITEM001 | BOX | 95.00 |
| ITEM001 | CTN | 900.00 |

The same item may therefore hold several prices, one per UOM — that is the legacy intent (the entry control carries an explicit "allow same item with diff UOM - diff price" comment) and it is why `IvCustPrice`'s key is `(CustPriceCode, ICode, UOM)` and `SaItemCust`'s app key includes `SellingUOM`.

Verified supporting facts: **no UOM conversion table exists in Blazor**; pack-size columns exist but are advisory (`IvCustPrice.SellPackSize` float, `SaItemCust.StdCustPSize` float, `IvStockMaster.StdPackSize` decimal(18,4)); `docs/sales_trans_enhancement.md` records that allocation currently requires strict UOM string equality and proposes a conversion factor — that is a separate initiative, not this one.

## A.7 Tenant scope matrix (§8) — recommended, per house precedent

| Entity | Company | Branch | Site / Location | Global | Recommended rule |
|---|---|---|---|---|---|
| `IvCustPriceGroup` | **key** | leftover stamp | leftover stamp | no | company-scoped master |
| `IvCustPrice` | **key** (inherited, must equal header's) | leftover stamp | leftover stamp | no | child inherits; never write a child in another company |
| `SaItemCust` | **key** | leftover stamp | leftover stamp | no | company-scoped assignment |
| `SaDisGroupItem` | **key** | leftover stamp | leftover stamp | no | company-scoped rule |

Rule to write down: **a record is visible and valid only within its company.** Branch/Location are stamped on create (the `InventoryLeftoverSite.Apply` convention) but are **not** key parts, because no consumer query needs them (legacy's tenant columns were not in any key — defects 13/14 — and the legacy delete helper that *did* filter all three used them as a scope filter, not an identity). **Fallback hierarchy: none** — no master lookup in this repo cascades branch → company → global; state that explicitly so nobody invents it. If branch-level pricing is ever required it needs its own consumer and an additive key, which is a Phase 0 finding, not an assumption.

Executable form: the matrix **is** the four new `InventoryLeftoverSite.Apply` overloads (N-12), plus a 2-company isolation test per table.

## A.8 Lifecycle: Active / Inactive / Deleted (§13) — verified column inventory

| Table | Legacy lifecycle column(s) | Verified meaning | Recommended in v2 |
|---|---|---|---|
| `IvCustPriceGroup` | **none** (only `Editable`, semantics undocumented) | legacy never deactivated a price list | add an additive `Active` (mirror `IvStockMaster.Active`) **or** keep absence — decide, because `SaCust.CustPriceCode` assignment lists must be able to exclude retired lists |
| `IvCustPrice` | none | — | no `Active`; removing a price = deleting the line |
| `SaItemCust` | `Status` = `NEW` / `FALSE` | **refresh flag**, not activation (drives the refresh workflow) | keep both values; expose **no** Active toggle |
| `SaDisGroup` (shipped) | `GroupStatus = 'NEW'` | activation — consumers filter on it | unchanged |
| `SaDisGroupItem` | `GroupStatus = 'NEW'` on add/save | written but **no** consumer filters it | align with `SaDisGroup` (filter) or drop the write — decide |

Phase 0 questions to ask verbatim: do legacy price groups/items have an active/status field? can an inactive group stay assigned to a customer? can an inactive rule be used by SO? does inactive mean "historical documents stay valid, new transactions cannot select it"?
Standing rule: **never hard-delete historical pricing because it is no longer active.** If a hard delete is chosen anywhere, the plan must state the reference policy that makes it safe (this is the same rule as C-9's "no cascade for business master data").

## A.9 `SaCustEntry` price-group picker — behaviour matrix (§15)

| # | State | Behaviour |
|---|---|---|
| 1 | initial value | show `Model.CustPriceCode` (the read-only detail pane at `SaCustEntry.razor:213` already displays it) |
| 2 | picker open | paged assignment list (N-7) — active price groups in the caller's company |
| 3 | selected | set the value, clear the field error |
| 4 | cancel | value unchanged |
| 5 | clear | blank is allowed (D-6 clause 1) |
| 6 | inactive selected | existing value remains shown; a *new* pick must come from the active list — do not silently blank an existing code |
| 7 | deleted / invalid existing value | **tolerated and flagged**, never blocking an unrelated edit (D-6 clause 3) — this is the migration-safety clause |
| 8 | validation failure | field-level error by key `"CustPriceCode"` (entry page holds a case-insensitive `ValidationErrors` dictionary, `SaCustEntry.razor.cs:42/144`) |
| 9 | save failure | keep the typed value; message-only failure for non-field errors |
| 10 | reload after save | navigate to the **list** route (repo convention) — never `NavigateTo` the same URL, which leaves a stale `_rowVersion` and makes the next save fail with a concurrency error |

## A.10 Phase 0 question bank (§23) — with what this repo already answers

| Group | Questions | Already answered here |
|---|---|---|
| Data model | business meaning of every table; business vs technical keys; tenant columns | partially — A.1, A.7 (keys still need the live index matrix) |
| Pricing | precedence; customer-item vs price-group; fallback; zero valid?; negative allowed?; currency stored or inherited? | currency **verified** (N-2); the rest are owner decisions, plus the **dealer-price storage** unknown (A.2 #4) |
| Discount | precedence; JOIN/SPLIT meaning; slot mapping; additive vs sequential; negative/over-100%; rounding | JOIN/cascade **verified** and tested; SPLIT **verified absent**; the rest are owner decisions |
| Effective dates | inclusive?; NULL?; overlap?; who wins? | `SaDisGroupItem` only (N-3); rules drafted in A.4 |
| Quantity | integer or decimal?; 0/0 valid?; `QtyFr`/`QtyTo` inclusive? | types **verified inconsistent** (N-4); rules drafted in A.5 |
| Lifecycle | active/inactive; delete; historical retention; existing invalid references | column inventory **verified** (A.8); policy is an owner decision |
| Scope | Company? Branch? Site? Global? fallback? | drafted in A.7 (no fallback exists in the repo) |
| Integration | currency, UOM conversion, tax, SO, Invoice, CN/DN | UOM conversion **verified absent** (A.6); tax out of scope but the **price basis must be declared** (N-1); SO/INV = contract now, implementation later (C-1/C-2) |

The point of the "already answered" column: Phase 0 should spend its budget on the **unknowns** — dealer-price storage, per-table live key/index/column matrix, row counts, external writers, `IvCustPriceGroup.Editable` semantics, `SaDisGroupItem.ID` PK/unique index, live 0/0 rows, and currency-mismatch behaviour — not on re-deriving facts already pinned above.

## A.11 Scorecard and gate (§26)

| Dimension | Review v2 score | After this plan pass |
|---|---|---|
| Architecture | 8/10 | unchanged (no refactor added) |
| ERP business-rule completeness | 6.5/10 | raised to the score the contracts reach **once C-1…C-4 and A.1–A.8 are answered** — they are drafted here, not yet decided |
| Blazor Server implementation readiness | 8/10 | + N-7 (lookup shape), A.9 (picker states) |
| Database/migration readiness | 7/10 | + N-4 (numeric policy), N-6 (key policy), N-11 (indexes), N-12 (tenant) |
| Security/permission design | 8/10 | + N-8 (declare projection vs hide-in-UI) |
| Testing strategy | 8/10 | + N-10 (duplicate race template), N-7 (paging test), N-12 (2-company isolation) |

Gate unchanged and now checkable against the decision register: **no Phase 1 until the decisions marked 🔴 in the register are closed in writing.**

## Fold-in checklist (Appendix A)

| # | Change | Target |
|---|---|---|
| 32 | Materialise the domain-model table (A.1) as v2 §4 | v2 doc §4 |
| 33 | Materialise the relationship map + candidate price-source list, incl. the dealer-price storage question (A.2) | v2 §7 |
| 34 | Materialise the discount pipeline + 10-question answer sheet (A.3) | v2 §8 |
| 35 | Adopt the effective-date standard and quantity-band standard, incl. the MOQ-fractional decision (A.4, A.5) | v2 §9 |
| 36 | Add the UOM scope statement and the multi-UOM example (A.6) | v2 §9 |
| 37 | Fill and adopt the tenant matrix + "no fallback" (A.7); the lifecycle inventory + policy (A.8) | v2 §6, §10 |
| 38 | Adopt the picker behaviour matrix incl. the post-save navigation rule (A.9) | v2 §14 |
| 39 | Split the Phase 0 question bank into "already answered" vs "must investigate" (A.10) | Phase 0 |
| 40 | Carry the scorecard + gate into Phase 0's exit criteria (A.11) | Phase 0 gate |

---

# Review v4 — decision-closure pass (2026-09-15)

Review v4 scored this file **8.7/10** as a correction plan and **7.2/10** for implementation readiness, and its recommendation was explicit: *do not spend another pass redesigning the architecture — the next pass must be decision closure.* That pass is done, and its output is a **separate clean document**.

## What was produced

`plans/sales-item-family-v2-plan.md` — the clean implementation plan in the 26-section structure, with every open question replaced by a recommended, evidence-backed decision and a 22-row sign-off register (§9). This file is now the change log (banner at the top).

## Disposition of the Review v4 blockers and improvements

| Review v4 item | Disposition | Where it now lives |
|---|---|---|
| **B1** price precedence undecided | **closed** — ordered algorithm, first valid candidate wins, fail closed at the end; `PriceMethod=DEALER` unsupported | clean plan §7 (D4), register row 4 |
| **B2** dealer-price storage | **closed as explicitly unsupported** in v2 — fail closed with a message, and no new table is invented; Phase 0 confirms the absence | clean plan §7 (D6), register row 7, §16 item 4 |
| **B3** `SPLIT` semantics | **closed** — `JOIN` = sum of percentages against the original price + amounts; `SPLIT` = sequential stack, i.e. the shipped engine behaviour is *correct* but was undocumented; both are now stated and pinned by tests so a future engine change cannot silently flip it | clean plan §8.1, register row 6 |
| **B4** tax basis | **closed** — all three stored prices are tax-exclusive, with a documented gross-up at the document boundary and a Phase 0 check of any existing seed site | clean plan §7.1 (D7), register row 8 |
| **B5** currency mismatch | **closed** — price lists are company base currency (no column, do not add one); `SaItemCust.Currency` mismatch **fails closed**, no implicit conversion | clean plan §7.1 (D8), register row 9 |
| **B6** lifecycle policy | **closed** — `Active` added to `IvCustPriceGroup` only; no `Active` on `IvCustPrice`; `SaItemCust.Status` stays a refresh flag with no Active toggle; `SaDisGroupItem.GroupStatus` is written but not authoritative; inactive rows stay valid for historical documents; never hard-delete historical pricing | clean plan §12.1, register row 16 |
| **B7** this file is not the implementation plan | **closed** — clean plan materialised at `plans/sales-item-family-v2-plan.md`; this file carries a CHANGE LOG banner; the correction→implementation split is documented rather than left implicit | banner; clean plan header |
| **M1** explicit business-key table | closed — business key / unique index / page key per table, plus the four-step natural-key policy | clean plan §8.8 |
| **M2** executable migration cases | closed — six cases incl. “incompatible type/length”, “duplicate business keys” and “NULL/blank tenant values”, each **STOP — DBA remediation required** rather than a silent alter | clean plan §13.2, register row 21 |
| **M3** concurrency tests per aggregate | closed — header update/update, line update/update, delete with stale row version, concurrent duplicate insert, concurrent overlapping band/date creation | clean plan §24 (concurrency row) |
| **M4** export security | closed — `VIEW_PRICE` gates the export endpoints/workbooks as well as screen projections | clean plan §11.1, register row 18 |
| **M5** audit actor | closed — `CreatedBy`/`ModifiedBy` come from authenticated server context only and are never accepted from a DTO | clean plan §5, register row 20 |
| Deployment/rollback (7.5/10 — steps not executable) | closed — deploy order, post-deploy verification, and the rule that additive DDL is *not* auto-reverted: rollback = deactivate menus + redeploy; destructive reversal is a separate DBA script with explicit approval | clean plan §25 |

## Sign-off gate (unchanged, now checkable)

The clean plan’s §9 register has 22 rows covering item authority, domain model, tenant scope, price precedence, discount precedence, JOIN/SPLIT, dealer price, tax basis, currency, effective dates, quantity bands, UOM, keys/duplicates, concurrency, DDL strategy, lifecycle, delete rules, `VIEW_PRICE`, the D-6 contract, audit actor, migration cases and the SO/INV consumer.

> **If any row is blank, Phase 0 = FAIL and no code is written.**

Expected readiness once the register is signed: **9.3–9.5/10** (Review v4’s own projection).

---

# Review v5 — audit of the clean plan (2026-09-15)

Review v5 scored this change log **9.3/10** and implementation readiness **8.9/10**, and its instruction was explicit: *do not review this file again — audit `plans/sales-item-family-v2-plan.md` directly for implementation-readiness and consistency.* That audit was performed and the **clean plan was amended** (now 618 lines). This file therefore stops here.

| Review v5 item | Audit outcome | Clean-plan change |
|---|---|---|
| **R1** the clean plan must be re-reviewed; 14 items must exist as executable text | all 14 were present, but two were prose-only | §7.2 and §8.1.1 now carry **worked examples**; §13.4 carries the definitive schema table |
| **R2** register needs owner / approval date / status | adopted | §9 gained `Owner`, `Approved`, `Status` columns; the approval record (name, date, version) is kept in the Phase 0 findings |
| **R3** “closed” must distinguish *policy decided* from *runtime fact verified* | adopted as a status vocabulary | `DECIDED` / `DECIDED + P0 verify` / `BLOCKED`; 12 of 24 rows are `+ P0 verify` |
| **R4** SPLIT needs numeric worked examples | added | §8.1.1 — JOIN 15.00 vs SPLIT 14.50 on 10 %+5 % at 100.00, plus percentage+amount, two amounts, zero, and the rejected inputs |
| **R5** price precedence needs worked examples | added | §7.2 — E1–E11: MOQ band selection, price-list fall-back, item default, UOM mismatch, DEALER, inactive item, currency mismatch, zero/blank price, stale `CustPriceCode` |
| **R6** definitive schema table | added | §13.4 — PK / business key / unique index / tenant / identity / concurrency / lifecycle per table, plus the live-vs-intended type divergences |
| three-stage gate | adopted | §16: Gate 1 register → Gate 2 Phase 0 live facts → Gate 3 plan consistency, made checkable by the new §9.1 traceability matrix |

## Defects found *by* the audit and fixed in the clean plan

1. **Key widening was assumed, not decided.** Legacy keys are *narrower* than the intended business keys (`IvCustPriceGroup` keys on `CustPriceCode` alone; `SaItemCust`'s designer PK is `(ICode, CustCode)`). §8.8 now presents it as a two-branch decision — **A** DBA widens the key (with duplicate + single-company checks, backup and reversal script) or **B** keep the legacy key and scope by company in the application — each with its recorded consequence (e.g. a `CustPriceCode`-only key means one price list code per *database*).
2. **`SaDisGroupItem.ID` is `smallint` in the legacy designer** — a 32,767-row ceiling. The plan previously made it the page key without noticing; §8.8 now flags the identity range as a Phase 0 check, a DBA-widening item, and a rule that no page key may rest on a type the table can outgrow.
3. **“At most one rule matches, so the runtime never guesses” was only true for new data.** Legacy's validator de-duplicated on `ICode + IClass + QtyFr + QtyTo` and relaxed even that for `0/0`, so overlapping rows can already exist. §8.3.1 adds a deterministic tie-break (class-specific → higher `QtyFr` → earlier `DateFr` → lower `ID`) with a logged warning, plus a Phase 0 overlap report and an owner decision (clean the data vs tolerate the tie-break).
4. **Live `float`/`int`/`datetime` columns vs “everything is `decimal(18,4)`”.** Re-typing a populated live column is the §13.2 STOP case, so §8.5/§13.4 now say: new columns are `decimal(18,4)`/`date`; **live columns keep their live type** with an explicit service-side scale, date windows compare on the date part, and any re-typing is a separate approved DBA migration.
5. **Width mismatches that would have broken validation.** `SaCust.CustPriceCode` is `nvarchar(20)` in Blazor while legacy `IvCustPriceGroup.CustPriceCode` is `nvarchar(10)`; `MsUom.UomCode` is `nvarchar(10)` while `SaItemCust.SellingUOM` is `nvarchar(5)`. §8.8 now requires the narrower width plus D-6 clause-3 tolerance for existing longer values.