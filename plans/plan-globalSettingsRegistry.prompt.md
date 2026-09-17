## Plan: Global Settings Registry (`dbo.AdSmParam`)

One dynamic-parameter table so Admin / Inventory / Sales / Procurement — and later Planning / Production / QA — settings stop needing a new `Company` column, while every value stays typed, scoped, server-read, and incapable of making behaviour unexplainable. Values live in the DB; the catalogue of legal keys lives in code, repeating the `SaCompanyPriceMethod` discipline (normalize, `NULL` = default, fail-soft) that already works in this repo.

**Objective.** Introduce one dynamic-parameter table so module settings stop needing a new column, while keeping every value typed, scoped, server-read, and impossible to make a behaviour "unexplainable".

**Scope boundary.** IN: new table, code catalogue, cached read service, admin screen, menu/permission deployment, docs, tests. OUT: migrating existing typed `Company` columns; secrets; ordering parameters; per-user UI preferences; wiring `ALLOW_NEGATIVE_STOCK`.

**Baseline at plan time.** `dotnet build ErpWeb.slnx` 0 errors · `dotnet test` 1423 passed / 0 failed / 0 skipped.

### Decision record (locked)

1. Scopes: `GLOBAL` + `COMPANY` + `BRANCH`; precedence Branch → Company → Global → code default.
2. Values: typed columns `ValueText / ValueNumber / ValueDate / ValueFlag` plus an exactly-one-populated CHECK.
3. Key authority: a code catalogue in `ErpWeb.Core`; unknown DB rows are ignored, never resolved.

### Invariants

- I1 No row means the code default, so the migration is behaviour-neutral.
- I2 A blank, unparseable, or unknown-token value falls through to the default; it never throws.
- I3 A definition's `AllowedScopes` beats the table — a disallowed row is ignored on read and rejected on write.
- I4 Reads are server-side only; no page posts a setting into a pricing or validation decision.
- I5 A setting selects among fixed named behaviours; it never defines an ordering.
- I6 `ErpWeb.Model` must not reference `ErpWeb.Core`.
- I7 A cache entry must never cross a tenant / company / branch boundary. The scope is part of the cache key and is never inferred from an unvalidated caller.
- I8 A successful `SaveAsync` or `ClearAsync` must render every affected snapshot unreachable, including snapshots that merely *fell back* to the changed value.
- I9 A bad **value** fails soft (I2). A missing **provider** or a missing **definition** fails loud. A wiring gap is a deployment defect, not a data condition, and must not be silently swallowed as "default".

### Review response (2026-09-17)

The plan was reviewed and conditionally approved (8.8 / 10). This revision resolves the four mandatory items and the three secondary requests.

| Review item | Resolution in this revision |
|---|---|
| 2.1 `ExistingColumn` mechanism left unspecified | New section "ExistingColumn projection contract" — an `IAppSettingValueProvider` abstraction, a DI registry, a catalogue-decides / provider-supplies split, a fail-fast rule for a missing provider (I9), and a provider-integrity test pair. |
| 2.2 Settings must not depend on `IInventoryTenantContext` | New **Phase P1.5**. Note the review assumed a shared context already existed; it does not — `IInventoryTenantContext` is used in **43 files** across Sales, Purchase, Admin and Numbering, so it is already the de-facto shared context and is merely misnamed. The answer is therefore **promote, do not fork**: a neutral `ITenantScopeContext` plus delegation, so there is still exactly one claim-normalisation implementation and zero call-site churn. |
| 2.3 Cache isolation and eviction unspecified | New section "Cache isolation and eviction" — composite scope key plus epoch versioning, an explicit per-scope invalidation matrix, I7/I8, and a stated multi-instance limitation. |
| 2.4 `IsSystem` semantics unspecified | **Removed from P0** and from the definition. It has no reader in v1, so it becomes a documented future column rather than an unused flag. See "The `IsSystem` decision". |
| 3 Concurrency clarification | New section "Concurrency contract" separating the insert race, the update race and a serialization conflict, with the `RowVersion`-on-the-edit-VM requirement. |
| 4 Catalogue must be the only path | New section "Catalogue authority — the only resolution path", enforced structurally rather than by convention, plus an orphan-row test. |
| 5 Move concurrency tests earlier | P2 now carries the insert-race, stale-`RowVersion` and cache-eviction tests. P7 is narrowed to the remaining cases and the `IsSerializationConflict` extraction. |

**Unchanged by this revision.** Everything the review marked as locked stays locked: the three scopes, the precedence ladder, typed value columns, the exactly-one-value CHECK, catalogue authority, unknown rows ignored, no row means default, no `ModuleCode` CHECK, no `EffectiveFrom`, `SALES.PRICE_METHOD` Company-only, no migration of existing `Company` columns, no secrets, no ordering parameters, no per-user preferences, and no `ALLOW_NEGATIVE_STOCK` wiring.

### Verified pre-flight facts

- 0.1 No settings/config table exists — 86 `DbSet`s, all business masters.
- 0.2 `MenuDeploymentParityTests.Every_declared_menu_code_is_present_in_the_shipped_menus_xml` means adding a `MenuCodes` constant without the `menus.xml` row fails the suite — one commit.
- 0.3 `MenuSyncService` soft-disables any `dbo.Menu` row absent from `menus.xml`; `AccessRightService` filters on `menu.IsActive`; admins bypass via `HasAdminBypass()` and mask the failure.
- 0.4 The XML's automatic `ACCESS` mapping only covers menus the sync *inserts*, so the seed script must insert `MenuPermission` rows explicitly.
- 0.5 `RoleMenuPermission` uses `IsAllowed`, not `IsActive`.
- 0.6 Reuse `IvMasterOperationResult<T>` and `IvMasterErrorCode` (already has `Concurrency`, `Validation`, `NotFound`, `InvalidScope`).
- 0.7 `IsSerializationConflict` is a private duplicate in `AdSmNumAdminService.cs:1080` and `SaSalesRefService.Lmw.cs:452` — extract, do not add a third copy.
- 0.8 The model layer must not reference Core (comment in `CompanyConfiguration.cs:36-38`).
- 0.9 `SaSalesRefService` is constructed in 8 test files, so do **not** add a constructor parameter.
- 0.10 Fixture template is `SaItemFamilySqlServerConcurrencyTests` (`IAsyncLifetime`, `TestConnectionKey`, `IsScratchDatabase`, `ERPWEB_REQUIRE_SQLSERVER_TESTS`).

### Phase map

| Phase | Deliverable | Blast radius | Depends on | Est |
|---|---|---|---|---|
| P0 | Schema + entity + configuration + `DbSet` — **DONE, verified 2026-09-17** | zero behaviour change | — | 2h |
| P1 | Catalogue + pure resolver + catalogue integrity tests — **DONE** | new files only | P0 | 4h |
| P1.5 | Shared tenant scope — promote, do not fork — **DONE** | 1 new file, 1 edit, 1 test-helper line, 0 call-site changes | P0 | 2h |
| P2 | `IAppSettingService` + providers + cache + DI + service, concurrency and cache-eviction tests — **DONE** | new files, DI lines | P1, P1.5 | 7h |
| P3 | Menu + permission deployment (3 artefacts, atomic commit) — **DONE + applied to dev and scratch** | one commit | — | 2h |
| P4 | `/admin/settings` screen — **DONE** | new page | P2, P3 | 8h |
| P5a | Read-only projection of `SALES.PRICE_METHOD` — **DONE** | none | P4 | 3h |
| P5b | First write consumer — **DONE**: `SALES.QUOTE_VALID_DAYS` wired into `SaQtService` + `SaQt.razor.cs` | 4 files | P5a | 3h |
| P6 | `docs/app-settings.md` — **DONE** | docs | P5b | 2h |
| P7 | Remaining concurrency cases + `IsSerializationConflict` de-duplication — **DONE** | 3 files | P2 | 2h |

Sequencing rules: P0 and P3 are independent. P1 and P1.5 must both precede P2. P3 must be one commit or the suite goes red. P5a must precede P5b.

**Execution rule (agreed with the review).** Do **P0 only** first. Review the generated SQL, entity and configuration, run the build and the full suite, and confirm the second script run is a clean no-op. Only then proceed to P1 / P1.5 / P2. The schema is the most expensive part to change later, so P0 is the correct first boundary. Do not start the admin screen until the P2 concurrency and cache-eviction tests are green.

**Steps**

**Phase P0 — Schema (no behaviour change)**
1. Add `create-adsmparam.sql` creating table `dbo.AdSmParam` with `ModuleCode`, `ParamKey`, `ScopeCode`, optional `CompanyCode`/`BranchCode`, four typed value columns, `IsActive`, `Remark`, audit columns and `RowVersion`; a `PK` on a surrogate `ParamId`; three CHECK constraints (scope token, scope-column consistency, exactly-one-value); a filtered unique index on the full key and a filtered lookup index on company + module + key.
2. Add entity `AdSmParam`, configuration `AdSmParamConfiguration` mirroring `AdSmNumConfiguration`, and one `DbSet` line in `AppDbContext` beside `AdSmNumDates`.
3. Confirm the build is clean and the suite is still 1423 / 0 / 0 — evidence the table changed nothing.

**Phase P1 — Catalogue and pure resolver**
4. Add `AppSettingModules`, `AppSettingDefinition`, `AppSettingCatalogue` under `ErpWeb.Core/Settings/`.
5. Add the pure `AppSettingResolver` mirroring `SaCompanyPriceMethod.Normalize`/`ResolveSources`: walk Branch → Company → Global, skip scopes the definition disallows, fall through on unknown tokens.
6. Land the seed catalogue — nine real keys plus one placeholder each for the three reserved modules — with `SALES.PRICE_METHOD` marked Company-only and column-backed.

**Phase P1.5 — Shared tenant scope (promote, do not fork)**
7. Add a neutral `ITenantScopeContext` and `TenantScope` under `ErpWeb.Core/Services/`, carrying `CompanyCode`, `BranchCode`, `LocationCode` and `UserId`, with `TryCompanyScope()`, `TryBranchScope()` and `TryWriteScope()`.
8. Move the claim normalisation currently inside `InventoryTenantContext` into that new implementation — trim, and return null when a claim is blank or exceeds its maximum (company 5, branch 5, location 10, user 10) — then have `InventoryTenantContext` delegate to it. `IInventoryTenantContext` keeps its exact public shape, so the **43 files** that use it, and the mocks in the test suite, are untouched. Register both in `CoreServiceCollectionExtensions`. `AppSettingService` depends on the neutral context and **never** on the inventory-named one.

**Phase P2 — Service, providers, cache, DI**
9. Add `IAppSettingService` and `AppSettingService` with explicit scope-plus-keys reads for services that already use `ValidateCompanyContext()`, plus a `GetForCurrentUserAsync` derived from `ITenantScopeContext`.
10. Add the `IAppSettingValueProvider` abstraction and its DI-indexed registry, and implement `SaPriceMethodSettingProvider` for `SALES.PRICE_METHOD` — see "ExistingColumn projection contract".
11. Wrap reads in `IMemoryCache` using the composite scope key and epoch versioning described in "Cache isolation and eviction". This is not an optimisation: `GetSalesPriceMethodAsync` is already one round trip per line-priced call, and `CurrentDateService` carries a "prefer cached company later" note.
12. Implement `Invalidate(scope, companyCode, branchCode)` exactly per the invalidation matrix, and call it only after a successful save or clear (never on a failed one).
13. Gate list/save/clear with `MenuCodes.AdminSettings` plus `ACCESS`/`EDIT` through `IAccessRightService`, following the `MsRefService.RequireBranchScopeAsync` pattern. Leave `GetValueAsync` ungated — it is a server capability like `ResolveLinePricingAsync`.
14. Enforce the save order defined by "Catalogue authority" and "Concurrency contract": resolve the definition first, reject an unknown key as `NotFound`, reject a disallowed scope as `Validation`, reject a column-backed write as `Validation` naming the owning screen, then validate the type and the allowed tokens.
15. Have the edit VM carry the `RowVersion` it loaded, map the entity's `RowVersion` with `IsRowVersion()` so EF emits a `WHERE RowVersion = @original` predicate, and translate a concurrency failure to `IvMasterErrorCode.Concurrency`.
16. Register the service in `CoreServiceCollectionExtensions`. Do not touch `SaSalesRefService`'s constructor (0.9).
17. Land the P2 tests **now, before any UI work**: the catalogue and provider integrity pair; the insert race; the stale-`RowVersion` update; eviction after save and after clear; non-leakage between two companies; and orphan-row ignoring.

**Phase P3 — Menu and permission, one atomic commit**
18. Add `MenuCodes.AdminSettings`, the `menus.xml` row, and `init-admin-settings-menu.sql` together.
19. Seed `MenuPermission` rows explicitly (ACCESS and EDIT) — the XML's automatic mapping only covers menus the sync inserts.
20. Resolve the `SortOrder` collision with `ADMIN_MASTER` (see Further Considerations 1).

**Phase P4 — Admin screen**
21. Build `/admin/settings` with a tab per module; each tab lists `AppSettingCatalogue.ForModule(module)` left-joined with rows so an unset setting shows `Default — <value>` rather than vanishing, and shows an orphan-row diagnostic count for rows outside the catalogue.
22. Choose the editor by declared type: a `Token` setting renders a combo bound to the allowed tokens, never free text — that is what makes hand-typed tokens impossible.
23. Carry `RowVersion` through the edit round-trip so the update race is detectable, and make "Clear" a DELETE, because the exactly-one-value CHECK makes nulling all four value columns illegal.
24. Render column-backed rows disabled with a link to the owning screen, and run the solution build afterwards since `dotnet test` never compiles `.razor`.

**Phase P5a — Prove the loop at zero risk**
25. Wire `SALES.PRICE_METHOD` through the provider built in step 10, reading `Company.SalesPriceMethod` and normalising with `SaCompanyPriceMethod.Normalize`. `SaveAsync` refuses column-backed definitions, so this is a view, not a second authority. Leave `GetSalesPriceMethodAsync` untouched.

**Phase P5b — First write consumer**
26. Wire the owner-chosen setting and add a regression test proving that with no row the behaviour is byte-identical to today — that is the invariant this phase exists to prove.

**Phase P6 — Docs**
27. Add `docs/app-settings.md` in the handover shape of `sales-price-engine.md`, including the "how to add a new setting" recipe (catalogue entry plus optional seed, no DDL), the delete-not-null rule, and the multi-instance cache staleness bound; cross-link from section 10 of `sales-price-engine.md`.

**Phase P7 — Hardening**
28. Add the remaining `AdSmParamSqlServerConcurrencyTests` cases on the `SaItemFamilySqlServerConcurrencyTests` fixture: soft-delete freeing the key, and branch plus company rows coexisting.
29. Extract the duplicated private `IsSerializationConflict` instead of writing a third copy.

**ExistingColumn projection contract**

A definition with `Backing = ExistingColumn` has its live value in an existing typed column and is served by a registered provider. The split of responsibility is deliberate:

- the **catalogue** decides that the setting exists, its type, its allowed scopes, its allowed tokens and its provenance label;
- the **provider** knows only how to read the owning column;
- the **service** never reads or writes `AdSmParam` for such a definition.

Contracts, in `ErpWeb.Core/Settings/`:

- `IAppSettingValueProvider` — `Module`, `Key`, and a single async read taking `AppSettingScope scope`, `companyCode`, `branchCode`. It returns the **raw** stored value, not a normalised one.
- Providers are discovered by DI (`IEnumerable<IAppSettingValueProvider>`) and indexed by `(Module, Key)` into a registry the service holds. Two providers claiming the same `(Module, Key)` is a startup error.
- An optional `Normalize` delegate on the definition is applied **after** the resolver has decided the value and its provenance, so the projection reports the same effective value the owning module uses (`SALES.PRICE_METHOD` supplies `SaCompanyPriceMethod.Normalize`) while an invalid stored token is still reported as rejected rather than silently corrected.

Read path for `SALES.PRICE_METHOD`: `Find("SALES", "PRICE_METHOD")` → lookup the provider → provider reads `Company.SalesPriceMethod` for the company → resolver applies the allowed tokens from `SaCompanyPriceMethod.All` → `Normalize` produces the effective value → provenance `Company`.

Three rules keep this safe:

1. **Never stored.** `SaveAsync` and `ClearAsync` reject `Backing = ExistingColumn` with `Validation` and a message naming the owning screen; the admin grid renders such rows read-only with a link. The registry therefore cannot become a second authority.
2. **Provider presence is enforced twice.** A catalogue integrity test asserts every `ExistingColumn` definition has exactly one provider and that no provider is orphaned; at runtime a missing provider throws a clear wiring error instead of quietly returning the default (I9). A bad *value* still fails soft (I2) — the distinction matters, because a missing provider is a deployment defect, not a data condition.
3. **`Backing` is never inferred.** A definition is `ExistingColumn` only because the catalogue says so. The service never guesses from the shape of the key.

**Cache isolation and eviction**

The cache key carries scope as identity, not as context:

`AppSetting:v1:{epoch}:{company ?? "*"}:{branch ?? "*"}:{module}`

where `{epoch}` is a composite version string built from three counters: a global counter, a per-company counter, and a per-(company, branch) counter.

**Invalidation matrix**, applied on every successful `SaveAsync` and `ClearAsync`:

| Write scope | Counter bumped | Effect |
|---|---|---|
| `GLOBAL` | global | every snapshot in the process becomes unreachable, because a global row is the last fallback for every tenant |
| `COMPANY` | that company | that company's snapshots become unreachable, **including its branch snapshots** — a branch snapshot that fell back to the company value is now stale |
| `BRANCH` | that (company, branch) | only that branch snapshot becomes unreachable |

**Why epochs rather than `IMemoryCache.Remove`.** `IMemoryCache` cannot enumerate keys by prefix, so "evict every affected snapshot" cannot be implemented reliably by removal — a partial eviction leaves a stale entry that no test will catch until a customer sees the wrong value. Bumping a counter that forms part of the key makes invalidation total and atomic, needs no locking, and cannot half-succeed. The abandoned entries expire on their own.

I7 and I8 make this testable: two companies reading the same module must never see each other's values, and a write at each scope must change what the next read resolves.

**Stated limitation.** The cache is per-process. In a multi-instance deployment, instance A's write does not evict instance B's snapshot; the 60-second absolute expiry bounds that staleness. That is acceptable for policy settings, but it must be written down in `docs/app-settings.md` rather than discovered later.

**The `IsSystem` decision**

`IsSystem` is **removed from P0** and from `AppSettingDefinition`.

It has no reader in v1. Keys are compile-time constants in the catalogue, unknown rows are ignored, and there is no admin-defined custom parameter — so "is this a system setting?" is already answered by the definition existing at all. A column plus a flag that nothing reads is exactly the kind of unused concept that later gets misinterpreted as a security control.

If the deferred "custom parameter" escape hatch is ever added, `IsSystem` returns with real semantics, and the rules are already decided:

- the **catalogue is authoritative**; the DB column is persisted metadata validated against it;
- `IsSystem` governs whether the *definition* may be deleted or disabled — **not** whether its *value* may be edited. A system setting is still a setting;
- a value may be edited; a definition may not disappear.

Adding it later is a cheap nullable-column `ALTER`, and the batching trap in "Traps" already covers the `ALTER`-then-reference case. Removing it now is cheaper than defending it.

**Concurrency contract**

Three distinct failures, and the result must not be collapsed into one:

| Case | Cause | Detection | Result |
|---|---|---|---|
| **Insert race** — two admins create the same logical key | filtered unique index `UQ_AdSmParam_Key` | `DbUpdateException` wrapping `SqlException` number **2601** or **2627** | the loser gets `Concurrency` |
| **Update race** — two admins load one row, A saves, B saves stale | `RowVersion` mismatch | EF `DbUpdateConcurrencyException`, or a zero-row `UPDATE` | B gets `Concurrency` with a reload-and-retry message |
| **Serialization conflict** — if any part of the save runs under `Serializable` | deadlock or serialization error (1205, 3960, 3961, 41301, 41302, 41325) | the existing `IsSerializationConflict` shape | `Concurrency` |

Requirements this imposes on the code:

1. The edit VM **must** carry the `RowVersion` it loaded, and the update path **must** set it as the original value. Without it the update race is undetectable and the second write silently wins — the worst outcome, because it is invisible.
2. `AdSmParam.RowVersion` must be mapped `IsRowVersion()`, not a plain byte column, or EF emits no predicate.
3. `ClearAsync` is a `DELETE` and must be `RowVersion`-checked too; otherwise two admins can both "clear" and the second either throws an unhandled error or silently no-ops.
4. A concurrency failure must never mutate the cache — invalidate only after a successful commit.
5. `Concurrency` stays its own `IvMasterErrorCode`; it is not folded into `Validation`, because the UI needs to say "reload", not "fix your input".

**Catalogue authority — the only resolution path**

The catalogue is not a validator bolted onto a free-form key/value store; it is the *only* way a key becomes a setting:

`ModuleCode + ParamKey` → `AppSettingCatalogue.Find(...)` → not found ⇒ ignore → found ⇒ `AppSettingDefinition` → `AppSettingResolver.Resolve(...)`

Enforced structurally rather than by convention:

1. `AppSettingResolver.Resolve` takes an `AppSettingDefinition`, never raw strings, and is `internal`. The only public entry points are on `IAppSettingService`.
2. `AppSettingService` is the sole caller of `Find`. No service anywhere resolves a setting by reading `AdSmParam` directly.
3. `ListForModuleAsync` enumerates `AppSettingCatalogue.ForModule(module)` and left-joins rows — it never derives keys with `SELECT DISTINCT` from the table. A manually inserted row therefore cannot appear as a new setting in the UI, which is the point.
4. A row whose `(ModuleCode, ParamKey)` is absent from the catalogue is **ignored on read** and surfaced only as an orphan-row diagnostic count. It never influences behaviour.
5. Two integrity tests: every definition's module is a member of `AppSettingModules.All` and `All` holds no duplicate key; and definitions round-trip through `Find` case-insensitively, so persisted casing can never miss.

**Relevant files**

- `scripts/create-adsmparam.sql` — new table, filtered unique index, batches split by `GO`
- `scripts/init-admin-settings-menu.sql` — model on `init-ms-dept-project-menu.sql` line for line
- `ErpWeb.Model/Entities/AdSmParam.cs` — columns only; must not reference Core
- `ErpWeb.Model/Configurations/AdSmParamConfiguration.cs` — mirror `AdSmNumConfiguration` (table name, max lengths, `date`/`datetime` types, `RowVersion`)
- `ErpWeb.Model/Data/AppDbContext.cs` — one `DbSet`, beside `AdSmNumDates`
- `ErpWeb.Core/Settings/AppSettingModules.cs` — module constants, three reserved
- `ErpWeb.Core/Settings/AppSettingDefinition.cs` — type, allowed scopes, default, allowed tokens, backing, optional `Normalize`, description
- `ErpWeb.Core/Settings/AppSettingCatalogue.cs` — the authority for what keys exist
- `ErpWeb.Core/Settings/AppSettingResolver.cs` — pure ladder, mirrors `SaCompanyPriceMethod`
- `ErpWeb.Core/Settings/IAppSettingService.cs`, `AppSettingService.cs` — reads, cache, gated writes
- `ErpWeb.Core/Settings/AppSettingRow.cs` — list row, edit VM (carrying `RowVersion`), provenance DTO
- `ErpWeb.Core/Settings/AppSettingValueProvider.cs` — `IAppSettingValueProvider` plus the `(Module, Key)` registry
- `ErpWeb.Core/Settings/SaPriceMethodSettingProvider.cs` — the `SALES.PRICE_METHOD` projection over `Company.SalesPriceMethod`
- `ErpWeb.Core/Services/TenantScopeContext.cs` — neutral `ITenantScopeContext` + `TenantScope`; the single claim-normalisation implementation
- `ErpWeb.Core/Inventory/InventoryTenantContext.cs` — edit: delegate to the neutral context; public shape unchanged
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — `AddMemoryCache`, `AddScoped<ITenantScopeContext, TenantScopeContext>`, `AddScoped<IAppSettingService, AppSettingService>`
- `ErpWeb.Core/Menus/MenuCodes.cs` — `AdminSettings = "ADMIN_SETTINGS"`
- `ErpWeb/Menus/menus.xml` — the `ADMIN_SETTINGS` row inside the `ADMIN` node, route `/admin/settings`
- `ErpWeb.UI/Admin/AdminSettings.razor(.cs/.css)` — model on `AdminCompany.razor.cs` (`EditModel`, `CanMutate`, `JsRuntime`)
- `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs` — reused by the P5a projection; `Normalize` is the fail-soft template
- `ErpWeb.Core/Inventory/IvMasterResults.cs` — reuse `IvMasterOperationResult<T>` / `IvMasterErrorCode`
- `ErpWeb.Tests/AppSettingCatalogueTests.cs` — catalogue integrity
- `ErpWeb.Tests/AppSettingServiceTests.cs` — precedence, scopes, fall-through, orphan-row ignoring
- `ErpWeb.Tests/AppSettingProviderTests.cs` — provider integrity and the projection path
- `ErpWeb.Tests/AppSettingCacheTests.cs` — key isolation, the invalidation matrix, no cross-company leakage
- `ErpWeb.Tests/AdSmParamSqlServerConcurrencyTests.cs` — fixture copied from `SaItemFamilySqlServerConcurrencyTests`
- `docs/app-settings.md` — new handover doc
- `docs/sales-price-engine.md` section 10 — add the cross-link

**Schema summary**

Table `dbo.AdSmParam`: `ParamId int IDENTITY` PK · `ModuleCode nvarchar(20)` · `ParamKey nvarchar(60)` · `ScopeCode nvarchar(10)` · `CompanyCode nvarchar(5) NULL` · `BranchCode nvarchar(5) NULL` · `ValueText nvarchar(400) NULL` · `ValueNumber decimal(28,10) NULL` · `ValueDate date NULL` · `ValueFlag bit NULL` · `IsActive bit` · `Remark nvarchar(400)` · `CreatedDate`/`CreatedBy`/`ModifiedDate`/`ModifiedBy` · `RowVersion rowversion`.

Constraints: `PK_AdSmParam` on `ParamId`; `CK_AdSmParam_Scope`; `CK_AdSmParam_ScopeCols`; `CK_AdSmParam_OneValue`.
Indexes: `UQ_AdSmParam_Key` on module + key + scope + company + branch (**not filtered**); `IX_AdSmParam_Lookup` on company + module + key including the value columns (**not filtered**).

**Deviation D1 — the indexes are NOT filtered, and this was proven necessary during implementation.**
The plan specified `WHERE IsActive = 1` on both indexes. Applying it to SQL Server surfaced error **1934**:
"INSERT failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'" — a filtered
index forces `SET QUOTED_IDENTIFIER ON` on **every** INSERT/UPDATE against the table, so ad-hoc DML and any
deploy script that does not set the option fails with a message that points nowhere near the cause.
The filter would only ever have excluded `IsActive = 0` rows, and those never exist here because clearing a
value DELETEs the row. So it bought nothing and cost a foot-gun. `IsActive` **stays** as a column (it is a
house convention across the repo's masters); only the filter is gone. The script self-heals: it drops and
recreates any index left filtered by an earlier run.
If a future soft-disable is genuinely introduced, the filter — and its SET-option requirement — should be
added deliberately at that point.

Deliberate choices: no CHECK on `ModuleCode`, because adding Planning/Production/QA later must not need DDL; no `EffectiveFrom`, because it would have to join the unique key and break one-active-row-per-key; no `IsSystem`, because nothing would read it in v1 (see "The `IsSystem` decision"); no seed rows, per I1.

Trap: "reset to default" DELETEs the row — nulling all four value columns violates `CK_AdSmParam_OneValue`.

**Core contracts**

- `AppSettingType` = Text | Number | Date | Flag | Token
- `AppSettingScope` = None | Global | Company | Branch (flags)
- `AppSettingBacking` = Registry | ExistingColumn
- `AppSettingDefinition(Module, Key, Type, AllowedScopes, DefaultValue, AllowedTokens, Backing, Normalize, Description)` — no `IsSystem`
- `IAppSettingValueProvider` — `Module`, `Key`, `ReadRawAsync(scope, companyCode, branchCode, ct)`; registered by DI and indexed by `(Module, Key)`
- `ITenantScopeContext` / `TenantScope` — neutral company / branch / location / user scope; `IInventoryTenantContext` delegates to it
- cache key shape — `AppSetting:v1:{epoch}:{company ?? "*"}:{branch ?? "*"}:{module}`
- `AppSettingCatalogue.All` / `Find(module, key)` / `ForModule(module)`
- `AppSettingResolver.Resolve(definition, branchRaw, companyRaw, globalRaw)` returning value, provenance, is-default and a rejection reason — pure, no DB, no DI
- `IAppSettingService`: `GetValueAsync`, `GetForCurrentUserAsync`, `GetFlagAsync`, `GetNumberAsync`, `ListForModuleAsync`, `SaveAsync`, `ClearAsync`. `SaveAsync` and `ClearAsync` both take an edit model carrying the loaded `RowVersion`.

**Catalogue seed**

| Module | Key | Type | Scopes | Default | Tokens |
|---|---|---|---|---|---|
| SALES | PRICE_METHOD | Token | Company | CUSTOMER_ITEM_AND_LIST | SaCompanyPriceMethod.All |
| SALES | ALLOW_BELOW_COST | Flag | Company, Branch | false | — |
| SALES | QUOTE_VALID_DAYS | Number | Company | 30 | — |
| INVENTORY | ALLOW_NEGATIVE_STOCK | Flag | Company, Branch | false | — |
| INVENTORY | QTY_DECIMALS | Number | Company | 4 | — |
| INVENTORY | REQUIRE_LOT_ON_RECEIPT | Flag | Company | false | — |
| PROCUREMENT | ALLOW_OVER_RECEIPT | Flag | Company | false | — |
| PROCUREMENT | REQUIRE_PR_APPROVAL | Flag | Company | true | — |
| ADMIN | SESSION_TIMEOUT_MINUTES | Number | Global | 60 | — |
| PLANNING/PRODUCTION/QA | one placeholder each | Flag | Company | false | — |

`SALES.PRICE_METHOD` is Company-only — never Branch, per section 5 rule 9 of `sales-price-engine.md` — and `Backing = ExistingColumn`.

**Verification**

1. Run `create-adsmparam.sql` twice; the second run must be a clean no-op that creates no index.
2. `dotnet build ErpWeb.slnx --nologo -v:q` must report 0 errors after every phase.
3. Set `ConnectionStrings__SqlServerTestConnection` and `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` as their own command, then run `dotnet test` separately. Expect `Failed: 0`, `Skipped: 0`, `Passed: 1423 + N`. `Skipped: 0` is the only proof the SQL tests ran, because an early return is reported as PASSED.
4. `MenuDeploymentParityTests` must be green immediately after P3, before any UI work.
5. Manual, on `http://localhost:5000`: `/admin/settings` opens; a non-admin without the grant is redirected to `/unauthorized`; save a COMPANY value, read it back from a second module, clear it, and confirm the code default returns; post a tampered scope and confirm the server rejects it.
6. P5a regression: manual smoke step 9 of `sales-price-engine.md` still passes unchanged.
7. P5b regression: with no `AdSmParam` row, the affected service returns exactly its pre-change value.
8. The scratch database must already exist — `CanConnect()` is probed before `EnsureCreatedAsync`.
9. Cache isolation: with two companies and the same module, each read resolves its own value; after a company write, that company's branch snapshots are invalidated too.
10. Concurrency: the insert race yields exactly one success and one `Concurrency`; a stale `RowVersion` update returns `Concurrency` and never silently overwrites.
11. Provider integrity: an `ExistingColumn` definition with no registered provider fails the catalogue integrity test, rather than silently resolving to the default at runtime.
12. Orphan rows: a manually inserted `(ModuleCode, ParamKey)` outside the catalogue never changes a resolved value, and appears only as a diagnostic count.
13. Catalogue containment: no value is resolved without passing through `AppSettingCatalogue.Find` — asserted by an API-surface test, not by inspection.

**Decisions**

- Scopes are `GLOBAL` + `COMPANY` + `BRANCH`, with precedence Branch → Company → Global → code default.
- Values are typed columns behind an exactly-one CHECK, rather than a single `ValueText`.
- Keys are compile-time constants in a Core catalogue; unknown DB rows are ignored, never resolved.
- No row means the code default, so applying the migration is behaviour-neutral.
- No CHECK on `ModuleCode`, so a later module costs no DDL.
- No `EffectiveFrom`, so the one-active-row-per-key guarantee survives.
- `SALES.PRICE_METHOD` is Company-only; `BranchCode` never enters a pricing key.
- Existing typed `Company` columns stay where they are; the registry is for genuinely scattered switches.
- Excluded: secrets, ordering parameters, per-user UI preferences, and wiring `ALLOW_NEGATIVE_STOCK`.
- The settings service depends on a **neutral** tenant scope and never on `IInventoryTenantContext`; the neutral context arrives by delegation, so exactly one claim-normalisation implementation exists.
- `IsSystem` is not in v1 — the existence of a catalogue definition is the only "system" signal required.
- A column-backed setting is a read-only projection, never a second write authority; `SaveAsync` refuses it.
- Cache invalidation is epoch-based rather than removal-based, because `IMemoryCache` cannot enumerate keys by prefix.
- A bad value fails soft, but a missing provider or an unknown definition fails loud (I9).
- **D1:** the indexes are unfiltered. A filtered index forces `SET QUOTED_IDENTIFIER ON` on every INSERT/UPDATE
  (SQL Server error 1934, hit and proven during P0) and excludes nothing, because clearing a value DELETEs the row.

### Implementation record (2026-09-17)

**P0 — schema**

| Check | Result |
|---|---|
| `dotnet build ErpWeb.slnx --nologo -v:q` | Build succeeded, **0 errors** |
| `scripts/create-adsmparam.sql` run 1 | `ADSM_PARAM_CREATED`, unique key and lookup index created |
| `scripts/create-adsmparam.sql` run 2 | `ADSM_PARAM_ALREADY_PRESENT`, **no index created** — clean no-op |
| Applied schema | 17 columns, 3 CHECK constraints, `PK` + `UQ_AdSmParam_Key` + `IX_AdSmParam_Lookup`, `has_filter = 0` on all three |
| Constraint smoke (rolled back) | `CK_AdSmParam_OneValue` rejects 0 and 2 value columns; `CK_AdSmParam_ScopeCols` rejects COMPANY-with-NULL-company and BRANCH-with-NULL-branch; `UQ_AdSmParam_Key` rejects a duplicate GLOBAL key; a valid GLOBAL and a valid COMPANY row insert |
| Table state | **0 rows** — behaviour-neutral, exactly as designed |

**Full delivery**

| Check | Result |
|---|---|
| Full suite | **1583 passed / 0 failed / 0 skipped** (baseline 1487 → **+96 new tests**, all accounted for: 90 registry + 6 SQL Server concurrency) |
| Per class | catalogue 15 · resolver 23 · service 36 · provider 6 · cache 10 · SQL concurrency 6 |
| `init-admin-settings-menu.sql` | applied to `ERPWeb` twice; run 2 a no-op; menu row `active=1` with `ACCESS,EDIT` |
| `create-adsmparam.sql` on `ERPWeb_test` | applied; needed because `EnsureCreatedAsync` never adds a table to an existing database |
| Solution build incl. the Razor page | 0 errors (`dotnet test` never compiles `.razor`) |

**Defects the tests found (both fixed, both documented in code)**

1. **Scope depth was read as an exclusive bitmask.** A read at `BRANCH` depth never loaded company or global
   rows, so every company default was silently discarded. A requested scope is an inclusivity LEVEL; the one
   definition now lives in `AppSettingService.Depth`.
2. **`Number` canonicalisation preserved trailing zeros**, so a value's textual form depended on how the
   storage engine round-tripped the decimal. `0.` + hashes now makes `45`, `45.0` and `45.00` one value.

**Deliberate, documented broadening in P7.** Consolidating the three copies of `IsSerializationConflict` into
`ErpWeb.Core/Services/SqlErrorClassifier.cs` added SQL codes `1222`, `3961`, `41301`, `41302` and `41325` to
callers that previously did not recognise them, and switched both to structured-number-first matching with a
message fallback. Every added code means the same thing to a caller — the write lost a race — and previously
one caller would have surfaced it as an unhandled exception instead of "reload and try again".

**New baseline: 1583.** The `1423` quoted earlier in this plan was stale; the `1487` measured at P0 was the
real baseline, and the +96 is exactly the new test count.

**Traps**

1. SQL Server compiles a whole batch before executing it, so `CREATE TABLE` and `CREATE INDEX` need separate `GO`s, and a later `ALTER` plus reference needs `sp_executesql`.
2. `RETURN` exits only its own batch, so every guard block needs its own existence check.
3. `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` or the concurrency class self-skips and reports PASSED.
4. `menus.xml` soft-disables absent codes; admins bypass and mask it.
5. `RoleMenuPermission.IsAllowed` is not `IsActive`.
6. `MenuPermission` rows must be seeded explicitly.
7. `NULL` counts as equal in a SQL Server unique index — that is what makes global-row uniqueness work.
8. The exactly-one-value CHECK means clear is a DELETE.
9. Any `Serializable` save must catch `IsSerializationConflict` and return `Concurrency`.
10. `IMemoryCache` has no prefix-removal API, so eviction must be epoch-based; a partial `Remove`-based eviction leaves stale entries that no test will catch until a customer sees a wrong value.
11. An optimistic-concurrency check without the original `RowVersion` on the edit VM is undetectable, and the second write then silently wins — an invisible failure, which is worse than an error.
12. A filtered unique index catches the insert race only if the `DbUpdateException` is unwrapped to the SQL error number; the outer message alone cannot distinguish a race from a validation failure.

**Further Considerations**

1. **Menu `SortOrder`.** `ADMIN_MASTER` currently holds 7. Option A: insert System Settings at 5 and renumber `ADMIN_MASTER` to 8 (cleanest grouping). Option B: append System Settings at 8 and touch nothing else (smallest diff). Option C: place it under the existing `ADMIN_MASTER` node instead of alongside it. Recommend A.
2. **First write consumer (P5b).** No hard-coded global policy constant was found: `PoVendorByItem` tolerance is per-vendor, `MaxDiscountPercent` of 100 is a correctness bound, and `DocumentNumberingService.MaxRetries` is technical. Option A: `SALES.QUOTE_VALID_DAYS` (low risk, recommended). Option B: `PROCUREMENT.REQUIRE_PR_APPROVAL`. Option C: `ADMIN.SESSION_TIMEOUT_MINUTES`. Option D: `INVENTORY.REQUIRE_LOT_ON_RECEIPT` (touches posting).
3. **`IsSerializationConflict` de-duplication (P7).** Option A: extract one shared internal helper (3 files touched). Option B: leave the two copies alone and let the new service hold its own, adding a third. Recommend A.
4. **Tenant context: delegate now, or rename fully?** The review asked for a shared context; the honest constraint is that `IInventoryTenantContext` is used in 43 files across Sales, Purchase, Admin and Numbering, so it *is* the shared context under an Inventory name. Option A: add the neutral `ITenantScopeContext`, have `InventoryTenantContext` delegate, and leave all 43 call sites alone (zero churn, one normalisation implementation, but two names for one concept survive). Option B: also sweep the rename of the 43 files in a separate mechanical commit with no behaviour change (cleaner end state, large diff, must be its own commit so review is trivial). Option C: sweep the rename inside this plan now (not recommended — it mixes a refactor with a feature). Recommend **A now, B as a follow-up**.
5. **Multi-instance cache staleness.** Option A: accept the 60-second bound and document it (recommended). Option B: shorten to 10 seconds for more freshness at the cost of more reads. Option C: add a version row polled by each instance for true cross-instance invalidation (real complexity; only if settings must take effect immediately across instances).
6. **Execution boundary — agreed.** P0 only first; review the SQL, entity and configuration, run the build and the full suite, confirm the second script run is a no-op, then continue. Recorded in the Execution rule above.

**Change log**

- 2026-09-17 — Created. Design decisions locked (GLOBAL/COMPANY/BRANCH, typed columns, code catalogue). Pre-flight facts verified by reading the repo. Baseline 1423 / 0 / 0.
- 2026-09-17 — Revised after external review (8.8 / 10, conditional approve). Added I7–I9 and the review-response mapping; added P1.5 (promote the tenant context, do not fork it) after verifying that `IInventoryTenantContext` is used in 43 files and no neutral context exists; specified the `ExistingColumn` provider contract, the cache key plus invalidation matrix, the concurrency contract (insert race vs update race vs serialization conflict) and catalogue containment; removed `IsSystem` from P0; moved concurrency and cache tests from P7 into P2; narrowed P7; recorded the P0-only execution rule.
- 2026-09-17 — **P0 implemented and verified.** Added `scripts/create-adsmparam.sql`, `AdSmParam`, `AdSmParamConfiguration` and the `DbSet`. Deviation **D1** recorded: index filters removed after SQL Server error 1934 proved a filtered index forces `SET QUOTED_IDENTIFIER ON` on every INSERT/UPDATE while excluding nothing. Script is idempotent and self-heals previously filtered indexes. 0 build errors; suite 1487 / 0 failed / 0 skipped; table left empty. **New baseline: 1487** (the 1423 in this document was stale and cannot be explained by P0).
- 2026-09-17 — **P1–P7 delivered in the same session** (P5b excepted, deliberately last). Added the catalogue, pure resolver, providers, epoch-versioned cache, service, `ADMIN_SETTINGS` menu + seed script, `/admin/settings`, the read-only `SALES.PRICE_METHOD` projection, `docs/app-settings.md`, the shared `ITenantScopeContext` and the shared `SqlErrorClassifier`. Suite **1583 / 0 failed / 0 skipped** (+96). Two real defects found by the new tests: scope depth was read as an exclusive bitmask (a branch-depth read silently discarded every company default) and `Number` canonicalisation preserved trailing zeros. P7's de-duplication deliberately broadened the recognised SQL error codes — see the Implementation record.
- 2026-09-17 — **P5b delivered.** `SALES.QUOTE_VALID_DAYS` is the first wired consumer: a day-count overload on `SaQtValidity.DefaultValidUntil`, a server-side read in `SaQtService` applied at all three write entry points and ONLY when the request omits `ValidUntil`, and the same window resolved by `SaQt.razor.cs` so the screen's pre-filled value cannot defeat the setting. 6 regression tests (`SaQtServiceTests`) pin that with nothing configured the result is byte-identical to the pre-setting behaviour, and that zero, negative, unparseable and blank values all keep the shipped default. Suite **1589 / 0 failed / 0 skipped**. Remaining unwired placeholders are listed in `docs/app-settings.md` §12.
