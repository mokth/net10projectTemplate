# Application Settings Registry — Status & Handover Reference

**Purpose.** A self-contained reference for the dynamic settings registry (`dbo.AdSmParam`): what it is,
what is finished, how to add a setting, how to verify it, and the rules that must not be broken. Written
so a future session can act on it **without** reading `plans/plan-globalSettingsRegistry.prompt.md`.

**Status date:** 2026-09-17 · **Verified:** solution build 0 errors; full suite **1589 passed / 0 failed /
0 skipped** (baseline 1487, so +102 new tests); SQL Server concurrency suite 6 passed / 0 skipped;
migrations applied and idempotent on `ERPWeb` and `ERPWeb_test` on `.\SQLEXPRESS`.

---

## 1. The system in ten lines

There is **no column per setting**. One table holds all of them, and **the set of legal settings lives in
code**, not in the table.

| Piece | Role |
|---|---|
| `dbo.AdSmParam` | Storage. A row is an **OVERRIDE only**. No row means the code default applies. |
| `AppSettingCatalogue` | The **authority**: which keys exist, their type, allowed scopes, allowed tokens and default. |
| `AppSettingResolver` | The pure ladder: Branch → Company → Global → code default. No DB, no DI. |
| `AppSettingService` | Loads, caches, validates and writes. The only caller of the catalogue. |
| `IAppSettingValueProvider` | Projects a setting whose live value already lives in a typed column. |

**A row whose key is not in the catalogue is ignored on read**, and appears only as a diagnostic count. **A
stored value that cannot be parsed, or a token that is not allowed, falls through to the default** — it
never throws and never wins. A settings table is read by every page; a mistyped value must degrade, not
break a document.

---

## 2. The shape of a setting

| Scope | Meaning | Needs |
|---|---|---|
| `GLOBAL` | One value for the whole installation | — |
| `COMPANY` | Overrides global for one company | `CompanyCode` |
| `BRANCH` | Overrides company for one branch | `CompanyCode` + `BranchCode` |

**A scope is an inclusivity LEVEL, not an exclusive bitmask.** Reading at `BRANCH` depth consults the
branch, then the company, then the global value. Reading at `COMPANY` depth stops before the branch.

> **This was a real bug, caught by the tests.** Treating the requested scope as a bitmask meant a
> branch-depth read never loaded company or global rows, so every company default was silently discarded.
> `AppSettingService.Depth` is the single place that defines the level semantics.

### Value types

| Type | Stored in | Canonical form |
|---|---|---|
| `Text` | `ValueText` | trimmed; an empty string is a legitimate value, blank means "not set" |
| `Token` | `ValueText` | one of `AllowedTokens`, case-insensitive on read, canonical on write |
| `Number` | `ValueNumber` | invariant culture, **trailing zeros dropped** so `45`, `45.0` and `45.00` are one value |
| `Date` | `ValueDate` | `yyyy-MM-dd` |
| `Flag` | `ValueFlag` | accepts `1/0`, `true/false`, `yes/no`, `y/n`; canonical `true`/`false` |

Trailing-zero normalisation is not cosmetic: without it a value's textual form depended on how the storage
engine round-tripped the decimal, so two equal numbers compared unequal as strings.

---

## 3. Adding a setting (no DDL)

1. Add a definition to `AppSettingCatalogue` — module, key, type, allowed scopes, default, and tokens for a
   `Token`.
2. Done. There is no migration, no column and no seed.

`AppSettingCatalogueTests` enforces the rest automatically: no duplicate `(Module, Key)`, a default that
parses as its own type, at least one token for every `Token`, every module declared, and a description so
the admin screen can explain the key.

Adding a **module** (Planning / Production / QA are already declared) needs only a constant in
`AppSettingModules` — which is why `ModuleCode` has no CHECK constraint.

> **A `Token` definition with no `AllowedTokens` throws.** That is deliberate: a token setting that cannot
> validate anything would silently accept whatever was stored. It is a catalogue bug, so it fails loudly.

---

## 4. The `ExistingColumn` projection

A setting whose live value already lives in a typed column is declared `Backing = ExistingColumn` and is
served by a registered `IAppSettingValueProvider`:

```
SALES.PRICE_METHOD  ->  Company.SalesPriceMethod  ->  SaCompanyPriceMethod.Normalize()  ->  effective value
```

The split is deliberate: the **catalogue** decides the setting exists and what its tokens are; the
**provider** knows only how to read the owning column; the **service** never reads or writes `AdSmParam`
for it.

Three rules keep it safe:

1. **Never stored, never written here.** `SaveAsync` and `ClearAsync` reject a column-backed definition
   with `Validation` and a message naming the owning screen. The registry cannot become a second authority
   for a value that already has a home.
2. **A missing provider fails LOUD (I9)** — an `InvalidOperationException`, not the default. A wiring gap
   is a deployment defect, not a data condition, and silently serving the default would hide it forever.
3. **The provider returns the RAW value.** Canonicalisation happens after provenance is decided, via the
   definition's optional `Normalize`, so a bad stored token is reported as rejected rather than quietly
   corrected.

`SaPriceMethodSettingProvider` projects the sales pricing method. The pricing method stays **Company-only**:
`BranchCode` must never enter a pricing key (see `docs/sales-price-engine.md` §5 rule 9).

---

## 4a. Wiring a setting into a consumer (the working example)

`SALES.QUOTE_VALID_DAYS` — the default offer window applied to a new quotation — is the reference
implementation. Copy this shape.

| Layer | Change |
|---|---|
| **Pure maths** | `SaQtValidity.DefaultValidUntil(qtDate, validityDays)` — a new overload; the 1-arg form still uses the constant, so every other caller is untouched. A day count of zero or less falls back to the shipped 30, because a quotation that is expired the moment it is created is never what an operator meant. |
| **Service (authoritative)** | `SaQtService.ResolveQuoteValidityDaysAsync` reads the setting through `IAppSettingService.GetValueAsync(..., AppSettingScope.Company, companyCode, null, ct)` and falls back to `SaQtValidity.DefaultValidityDays` when the value is missing or nonsense. Applied at all three write entry points (save new, save edit, revise) and **only when the request omitted `ValidUntil`** — an explicit value always wins. |
| **Screen** | `SaQt.razor.cs` resolves the window in `LoadValidityDaysAsync` before `ResetNewDocument`, and reuses it when the quotation date moves. A failed read leaves the shipped default, so the screen still works. |

**Why both layers are required.** The service carries the rule — a caller can omit `ValidUntil` and get the
company's window. But the screen **pre-fills** `ValidUntil`, and an explicit value always wins, so a screen
that still said 30 would silently defeat the setting. Both read the same setting through the same service.

**The read is server-side.** A client can never name the window; it can only omit the date and let the
server fill it (I4).

**Regression pinned by 6 tests in `SaQtServiceTests`:** the configured window is used; **with nothing
configured the result is byte-identical to before the setting existed**; and zero, negative, unparseable and
blank values all keep the shipped default.

---

## 5. Cache isolation and eviction

Key: `AppSetting:v1:{epoch}:{company ?? "*"}:{branch ?? "*"}:{module}`
where `{epoch}` is a composite of three counters — global, per-company, per-(company, branch).

| Write scope | Counter bumped | Effect |
|---|---|---|
| `GLOBAL` | global | **every** snapshot becomes unreachable — a global row is the last fallback for every tenant |
| `COMPANY` | that company | that company's snapshots **and its branch snapshots** become unreachable |
| `BRANCH` | that (company, branch) | only that branch snapshot |

**Why epochs, not `IMemoryCache.Remove`.** `IMemoryCache` cannot enumerate keys by prefix, so "evict every
affected snapshot" cannot be implemented reliably by removal — a partial eviction leaves a stale entry that
no test catches until a customer acts on the wrong value. Bumping a counter that forms part of the key makes
invalidation total, atomic and lock-free. Abandoned entries expire on their own.

**Multi-instance limitation (written down, not discovered later).** The cache is per-process. Instance A's
write does not evict instance B's snapshot; the **60-second absolute expiry** bounds that staleness. That is
acceptable for policy settings. If settings must take effect immediately across instances, the fix is a
version row polled by each instance — not a shorter TTL.

---

## 6. Load-bearing rules — do not break these

1. **The catalogue is the ONLY way a key becomes a setting.** `AppSettingResolver.Resolve` is `internal` and
   takes a definition, never raw strings; `AppSettingService` is the sole caller of `Find`; nothing anywhere
   reads `AdSmParam` to resolve a setting.
2. **A scope the definition does not allow is ignored on read and rejected on write.** The table never
   widens a definition.
3. **A bad value fails soft; a missing provider fails loud.** Do not blur these.
4. **"Clear" is a DELETE**, never a null-out. `CK_AdSmParam_OneValue` requires exactly one value column to
   be populated, so blanking all four violates it. Deleting also keeps the unique key free — which is why
   the index carries no `IsActive` filter.
5. **The indexes are NOT filtered.** A filtered index forces `SET QUOTED_IDENTIFIER ON` on every
   `INSERT`/`UPDATE` against the table (SQL Server error **1934**) and would exclude nothing, because
   clearing deletes the row. The trap cost a run during implementation. The script self-heals: it drops and
   recreates any index left filtered by an earlier run.
6. **An update must carry the `RowVersion` it loaded.** Without it a concurrent update is undetectable and
   the second write silently wins — an invisible failure, which is worse than an error.
7. **Invalidate the cache only after a successful commit.** A failed write must not persuade the cache that
   anything changed.
8. **Reads are not menu-gated; list/save/clear are.** A pricing or posting decision must be able to ask for
   a setting without the caller holding a settings-screen permission. This mirrors
   `ResolveLinePricingAsync`, which is likewise deliberately ungated.
9. **A setting may select among fixed named behaviours; it may never define an ordering.**
10. **SQL Server treats `NULL` as equal in a unique index**, which is what keeps `GLOBAL` rows (both scope
    columns NULL) unique. On **SQLite NULLs are distinct** and `AppDbContext.OnModelCreating` strips index
    filters, so duplicate-key behaviour in the SQLite tests rests on the service's pre-check; the actual
    race is covered by `AdSmParamSqlServerConcurrencyTests`.

---

## 7. Deployment

### Script order

| Situation | Steps |
|---|---|
| **Any database** | `scripts/create-adsmparam.sql` (idempotent, self-healing), then `scripts/init-admin-settings-menu.sql`. |

No seed rows are inserted: an empty table means every setting uses its code default, so applying these
scripts **changes no behaviour**. Both were verified twice on `ERPWeb` and once on `ERPWeb_test`, the second
run a clean no-op.

### The scratch test database needs the table

`AppDbContext.EnsureCreatedAsync` creates the whole model only when the database does not exist — it **never
adds a table to an existing one**. An `ERPWeb_test` created before this feature will lack `AdSmParam`, and
`AdSmParamSqlServerConcurrencyTests` will say so explicitly (and fail, when
`ERPWEB_REQUIRE_SQLSERVER_TESTS=1`). Fix: apply `scripts/create-adsmparam.sql` to the scratch database.

### Menu + permission

`MenuCodes.AdminSettings`, the `menus.xml` row and `init-admin-settings-menu.sql` are **three separate
artefacts and all three are required**. Seeding a menu row grants nothing: the seed creates `ACCESS` and
`EDIT` on the menu, but a **role** still needs a `RoleMenuPermission` row — and that table uses
**`IsAllowed`, not `IsActive`** (the one grant table that differs). The snippet is at the bottom of the seed
script.

`menus.xml` is synced on every startup and **soft-disables** any `dbo.Menu` row whose code is absent from
the XML, so a missing XML row means `/unauthorized`.
`MenuDeploymentParityTests` guards the constant ↔ XML pair.

---

## 8. Where the pieces live

| Looking for | Go to |
|---|---|
| Which settings exist, their type/scope/default/tokens | `ErpWeb.Core/Settings/AppSettingCatalogue.cs` |
| The resolution ladder | `ErpWeb.Core/Settings/AppSettingResolver.cs` |
| Level semantics (Branch ⊃ Company ⊃ Global) | `AppSettingService.Depth` |
| Cache key + invalidation matrix | `ErpWeb.Core/Settings/AppSettingCacheVersions.cs`, `AppSettingService.Invalidate` |
| The read-only projection contract | `AppSettingValueProvider.cs`, `SaPriceMethodSettingProvider.cs` |
| The shared SQL error classifier | `ErpWeb.Core/Services/SqlErrorClassifier.cs` |
| The shared tenant scope | `ErpWeb.Core/Services/TenantScopeContext.cs` |
| The screen | `ErpWeb.UI/Admin/AdminSettings.razor(.cs)` at `/admin/settings` |
| The table | `ErpWeb.Model/Entities/AdSmParam.cs`, `Configurations/AdSmParamConfiguration.cs`, `scripts/create-adsmparam.sql` |

### The settings service is module-neutral

It depends on `ITenantScopeContext` (`ErpWeb.Core/Services`), **not** on `IInventoryTenantContext`.
`InventoryTenantContext` now **delegates** to the same implementation, so there is exactly one definition of
what a valid company code is. Do not add normalisation logic to `InventoryTenantContext` — change
`TenantScopeContext`, or the two will drift and one module will accept a code another rejects.

---

## 9. Test map

| Test class | Tests | Covers |
|---|---|---|
| `AppSettingCatalogueTests` | 15 | duplicate keys, defaults parse as their own type, tokens declared, modules known, `Find` case-insensitivity, the price method is Company-only and column-backed |
| `AppSettingResolverTests` | 23 | the ladder, blank = not set, disallowed scope ignored, unknown token falls through and is reported, flag/number/date canonicalisation, the missing-token catalogue error |
| `AppSettingServiceTests` | 36 | precedence through the database, scope containment, cross-company isolation, the projection and its read-only refusal, listing, gating, save/clear, stale `RowVersion` |
| `AppSettingProviderTests` | 6 | provider integrity both ways, duplicate-provider startup error, the missing-provider throw |
| `AppSettingCacheTests` | 10 | the invalidation matrix as pure logic, then end to end: no cross-company leakage, company write invalidates that company's branch snapshot, global write invalidates everything, clear invalidates |
| `AdSmParamSqlServerConcurrencyTests` | 6 | real insert race, real server `rowversion`, clear frees the key, two companies at once, company + branch coexistence, a registry row cannot hijack the projection |

**Fixture hazard worth knowing:** `AppSettingTestHost.SeedAsync` writes directly to the database, so it
**does not invalidate the cache**. Seed every row before the first read of that module, or bump
`AppSettingCacheVersions` afterwards. One test initially failed for exactly this reason — it was a fixture
mistake, not a product bug.

---

## 10. Verification

```powershell
dotnet build ErpWeb.slnx --nologo -v:q
```

```powershell
# Set these as their OWN command, then run dotnet test separately.
$env:ConnectionStrings__SqlServerTestConnection = 'Server=.\SQLEXPRESS;Database=ERPWeb_test;Trusted_Connection=True;TrustServerCertificate=True;'
$env:ERPWEB_REQUIRE_SQLSERVER_TESTS = '1'
```

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --nologo
```

Two traps that cost real time:

1. **`Skipped: 0` is the evidence that matters.** Most SQL Server concurrency classes `return` early when
   no scratch server is configured, and an early return is reported as **PASSED**. With
   `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` a self-skip becomes a failure, so `Skipped: 0` is the only proof the
   SQL tests genuinely executed.
2. **`ErpWeb.Tests` does NOT reference `ErpWeb.UI`**, so `dotnet test` never compiles a `.razor` or
   `.razor.cs` file. **Always run `dotnet build ErpWeb.slnx` after touching the settings page.**

Run per area while iterating:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --nologo --filter "FullyQualifiedName~AppSetting"
```

---

## 11. Manual smoke (not yet performed)

Needs the app running (`dotnet run --project ErpWeb`, `http://localhost:5000`).

| # | Step | Pass condition |
|---|---|---|
| 1 | Open `/admin/settings` | a tab per module; unset settings show their code default, not a blank |
| 2 | Sign in as a user without `ADMIN_SETTINGS` + `EDIT` | the screen is view-only; the save button is absent |
| 3 | Set `QUOTE_VALID_DAYS` at Company scope to 45 | the effective value changes and the source column reads "Company" |
| 4 | Press **Clear** | the row is deleted and the default (30) returns |
| 5 | Look at `SALES.PRICE_METHOD` | read-only, says Read-only, and "Open owning screen" lands on `/admin/company` |
| 6 | Change the pricing method on `/admin/company`, return | the settings row reflects the new value with provenance Company |
| 7 | Set a value with a hand-crafted POST to a scope the definition forbids | rejected server-side (not merely hidden in the UI) |

---

## 12. Out of scope, deliberately

| Excluded | Reason |
|---|---|
| Migrating `Company.SalesPriceMethod`, `CurrencyCode`, `TimeZoneId`, `FiscalYearStartMonth` | each already has a typed column, a UI and tests; a parallel registry row would create two authorities for one truth |
| `EffectiveFrom` / `EffectiveTo` | it would have to join the unique key and break one-active-row-per-key |
| Secrets (connection strings, API keys) | the table is readable by anyone with the screen |
| Ordering or sequencing parameters | rule 9 above — add a named behaviour instead |
| Per-user UI preferences | a different concern; belongs in the UI layer |
| DB-defined free-form keys | keys are compile-time constants |
| `AllSettingsForCurrentUserAsync` / batch reads | nothing needs them yet; `ListForModuleAsync` is the screen's shape |
| Wiring the remaining placeholders | `ALLOW_BELOW_COST`, `ALLOW_NEGATIVE_STOCK`, `QTY_DECIMALS`, `REQUIRE_LOT_ON_RECEIPT`, `ALLOW_OVER_RECEIPT`, `REQUIRE_PR_APPROVAL`, `SESSION_TIMEOUT_MINUTES` and the three reserved module placeholders are declared but **not consulted by any consumer** — only `QUOTE_VALID_DAYS` is wired. Each needs its own change and its own regression test; negative stock touches posting integrity and should not ride along. |

---

## 13. Change log

- **2026-09-17** — Created at feature completion. Table, catalogue, resolver, service, cache, provider
  projection, admin screen, menu/permission deployment, shared `ITenantScopeContext`, shared
  `SqlErrorClassifier`, **96 new tests** (90 registry + 6 SQL Server concurrency). Verified: build 0 errors;
  full suite 1583 passed / 0 failed / 0 skipped; migrations applied and idempotent on `ERPWeb` and
  `ERPWeb_test`. Two defects were found by the tests and fixed: scope depth was read as an exclusive bitmask,
  and `Number` canonicalisation preserved trailing zeros.
- **2026-09-17** — **First consumer wired.** `SALES.QUOTE_VALID_DAYS` now drives the default offer window on a
  new quotation, in both `SaQtService` and `SaQt.razor.cs`; see §4a for the pattern. **+6 tests** (1589 total),
  including the explicit assertion that with no setting configured the behaviour is unchanged.
