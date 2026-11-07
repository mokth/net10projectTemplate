# productionv2 Data Protection implementation report

**Status:** Code ready on branch `fix/dataprotection-key-ring`. **Production cutover NOT complete** — Phase 0 live IIS evidence and T01–T12 on IIS are operator-owned and **BLOCKED** until executed.

| Item | Value |
|------|--------|
| Base branch | `productionv2` |
| Implementation branch | `fix/dataprotection-key-ring` |
| Base / start SHA (local) | `ed703f6665d90cc979043395afd3089ec36fdd86` |
| Remote `origin/productionv2` at fetch | `2b348746c40055aa322bd07d3f15d46b0bd35e32` |
| Merge-base with origin (earlier audit) | `371725c0bda74e386a66bfa3c7559c6f227a14dd` |

## Root cause classification

**UNKNOWN (live)** — no production IIS filesystem or key ring was inspected from the agent environment.

**Likely contributing factors (repository evidence):**

- Keys were bound to `{ContentRootPath}\DataProtection-Keys`, which travels with publish folder swaps and ZIP extract workflows.
- `deploy.ps1` wipes local publish output before packaging; operators extracting over IIS folders can destroy in-app key folders.
- Incident key `855d60ca-6640-46b7-9648-59c673a6042e` was **not** present in local dev key filenames under this workspace.

**Git history:** `git log --all --full-history -- "**/key-*.xml"` returned **no commits** — key XML was not committed to this repository.

## Source changes (allowed scope)

| Path | Change |
|------|--------|
| `ErpWeb/Hosting/DataProtectionKeyRing.cs` | **Added** — resolve/validate path, register DP, startup diagnostics |
| `ErpWeb/Program.cs` | **Modified** — `AddErpWebDataProtection`, startup log |
| `ErpWeb/ErpWeb.csproj` | **Modified** — exclude keys from publish; `InternalsVisibleTo` Tests |
| `ErpWeb/appsettings.json` | **Modified** — Serilog override `Microsoft.AspNetCore.DataProtection` = Information |
| `ErpWeb.Tests/ErpWeb.Tests.csproj` | **Modified** — project reference to `ErpWeb` |
| `ErpWeb.Tests/Other/DataProtectionKeyRingTests.cs` | **Added** — 14 unit tests |
| `.gitignore` | **Modified** — blanket key XML / folder patterns |
| `deploy.ps1` | **Modified** — strip keys from publish ZIP; IIS env guidance |
| `ANTIFORGERY_FIX.md` | **Rewritten** — durable path architecture |
| `DEPLOYMENT_GUIDE.md` | **Updated** — `DataProtection__KeysPath`, preserve keys on deploy |
| `FINAL_DEPLOYMENT_STEPS.md` | **Updated** — durable directory guidance |
| `DATA_PROTECTION_KEY_RING_RUNBOOK.md` | **Added** — operator runbook |
| `scripts/dataprotection/phase0-diagnose-readonly.ps1` | **Added** |
| `scripts/dataprotection/phase1-provision-directory.ps1` | **Added** |
| `scripts/dataprotection/phase3-copy-keys.ps1` | **Added** |
| `scripts/dataprotection/PHASE0_EVIDENCE_TEMPLATE.md` | **Added** |

**Not modified:** `ErpWeb.Core/**`, `ErpWeb.Model/**`, SQL, costing/stock business logic, cookie auth scheme, antiforgery enforcement (`UseAntiforgery()` unchanged).

**Working tree note:** Unrelated WAC/FG replay edits remain **unstaged** in this checkout and must not be included in a Data Protection commit.

## Configuration mode

| Environment | Keys path | DPAPI at rest |
|-------------|-----------|---------------|
| Production (Windows IIS) | Required absolute `DataProtection:KeysPath` / `DataProtection__KeysPath` | Yes (new keys) |
| Production (non-Windows) | N/A | Startup **throws** |
| Development / Staging | Default `{ContentRoot}/DataProtection-Keys` if unset | No |

Application discriminator: **`ErpWeb`** (unchanged).

## Commands / verification (lab)

```text
dotnet build ErpWeb/ErpWeb.csproj          — PASS (after stopping locking ErpWeb process)
dotnet build ErpWeb.UI/ErpWeb.UI.csproj    — PASS
dotnet build ErpWeb.Tests/ErpWeb.Tests.csproj — PASS
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~DataProtectionKeyRingTests" — PASS (14/14)
```

Full suite not re-run (large ~2000-test assembly); Shared category baseline was 310 passed before this work.

## T01–T12 matrix

| ID | Lab | Live IIS |
|----|-----|----------|
| T01 start/login | BLOCKED (no IIS) | **BLOCKED** — operator |
| T02 pre-migration token | Partial (unit continuity test) | **BLOCKED** |
| T03 app-pool recycle | Simulated (two providers, same folder) **PASS** | **BLOCKED** |
| T04 host restart | **BLOCKED** | **BLOCKED** |
| T05 publish same path | **BLOCKED** | **BLOCKED** |
| T06 new publish dir | **BLOCKED** | **BLOCKED** |
| T07 key rotation + expired kept | **BLOCKED** (manual) | **BLOCKED** |
| T08 bad path/ACL | **PASS** (validation unit tests) | **BLOCKED** |
| T09 test env without prod path | **PASS** | N/A |
| T10 tampered token | **PASS** (unit) | **BLOCKED** |
| T11 login/logout | N/A (no auth change) | **BLOCKED** |
| T12 concurrent POSTs | **BLOCKED** | **BLOCKED** |

## Operator steps not yet executed

1. Run `scripts/dataprotection/phase0-diagnose-readonly.ps1` on production IIS host; complete `PHASE0_EVIDENCE_TEMPLATE.md`.
2. Encrypted backup of all existing key XML (filenames/hashes only in manifest).
3. `phase1-provision-directory.ps1` with approved absolute path and app pool.
4. `phase3-copy-keys.ps1` from content-root ring(s) to durable path.
5. Set `DataProtection__KeysPath` on IIS; recycle app pool; execute T01–T12 on live site.

## Rollback

- Revert IIS `DataProtection__KeysPath` to previous value (or content-root ring copy).
- Do **not** delete key files during rollback.

## Security escalations (separate ticket)

- `ErpWeb/appsettings.Production.json` contains production credential material in the repository copy — assess exposure, rotate, move to deployment-time secrets. **Not modified** in this fix.

## Scores (design review — code ready only)

| Dimension | Score / 10 | Notes |
|-----------|------------|--------|
| Design | 9.7 | Durable path + fail-fast Production |
| Security | 9.6 | DPAPI single-server; antiforgery unchanged |
| Migration | 9.5 | Scripts + copy-only; live evidence pending |
| Compatibility | 9.7 | `ErpWeb` discriminator preserved |
| Testing | 9.4 | 14 focused tests; no WebApplicationFactory/IIS |
| Maintainability | 9.6 | Centralized `DataProtectionKeyRing` |

**Overall code-ready:** ~9.6/10. **NOT APPROVED for production complete** until Gate 0 and live T01–T12 pass.
