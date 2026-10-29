# Global inline search UI standardization (productionv2)

Draft PR body. Target `productionv2`. Supersedes PR #2 (`ui/standard-inline-search-20261029`). **Do not merge until the browser/visual matrix is completed.**

## What this changes

Every applicable ERP search surface now renders one non-wrapping row:

`Caption  [ search text input ][🔍]`

- Caption is a real `<label for>` bound to the editor `InputId`, truncated with an ellipsis instead of wrapping.
- The editor keeps a usable width floor (`8rem` desktop, `6rem` under 576 px) and never scrolls the magnifier out of view.
- The action is an icon-only `fa-solid fa-magnifying-glass` button with an accessible name, immediately adjacent to the input.
- Enter and the magnifier apply the same text immediately (`BindValueMode.OnInput` plus the existing Enter handler), and pending debounce work is cancelled on every search-affecting change and on disposal.
- Live filter rows keep no inert button; genuine multi-criteria forms keep their single shared action.

## Scope

UI-only. 107 files:

- 62 `ErpWeb.UI` Razor screens, popups and transaction pickers
- 2 scoped `.razor.css` files and 1 shared stylesheet (`inventory-chrome.css`)
- 41 `.razor.cs` files, limited to search wiring, debounce cancellation and disposal bookkeeping
- 1 source-contract test file

Nothing under `ErpWeb.Core/`, `ErpWeb.Model/`, `ErpWeb.Data/`, no SQL/migration, no entity, API, permission, costing, valuation, posting, rollback or inventory-quantity code. Business-logic diff: **NONE**.

## Audit

- 254 Razor files audited (250 `ErpWeb.UI` locally vs 249 on `productionv2`; the extra file is the local-only `ErpOperatorFailureBanner.razor` from `8d09a22`, not part of this branch, plus 4 host files)
- 64 search controls: **56 converted, 8 documented exceptions, 0 blocked**
- Per control: `plans/UI_SEARCH_CONTROL_AUDIT.md`
- Evidence matrix: `plans/UI_SEARCH_EVIDENCE_MATRIX.md`
- Method-level change audit: `plans/evidence/change-audit.md`
- Full report: `plans/UI_SEARCH_STANDARDIZATION_COMPLETION_REPORT.md`

## Verification

| Check | Result |
| --- | --- |
| `dotnet build ErpWeb.slnx` | PASS, 0 errors |
| `dotnet build ErpWeb.UI/ErpWeb.UI.csproj` | PASS, 0 errors |
| `dotnet build ErpWeb/ErpWeb.csproj` | PASS, 0 errors |
| `dotnet test --filter ErpInlineSearchContractTests` | PASS 6/6 |
| `dotnet test ErpWeb.Tests` (delivery) | 4592 total, 4557 passed, 27 failed, 8 skipped |
| Baseline A (`8d09a22`) | 4782 total, 4747 passed, 27 failed |
| Baseline B (`origin/productionv2` `371725c`) | 4586 total, 4551 passed, 27 failed |
| Failure attribution | Delivery failure set identical to both baselines; 27/27 reproduced on pristine Baseline B; **0 new failures** |
| `git diff --check` | PASS |
| Denylist audit | PASS, zero forbidden paths |
| Live browser, 56 converted controls | 43 PASS, 13 NOT VERIFIED |
| Responsive 320 / 375 / 768 / 1280 / 1920 px | PASS, single-line row, no wrap, magnifier 36 px and unclipped at every width |
| Constrained popup at 200 % zoom equivalent | PASS, 218 px input and 36 px magnifier fully inside a 350 px popup |
| Theme | Light PASS; dark NOT VERIFIED (no discoverable toggle in the running host) |

Deep UAT covered `#admin-company-rail-search` (live filter 2 → 1 → 0 → cleared), `#iv-stock-master-list-search` (debounce 11 → 2, icon click before debounce expiry honours the latest text, Enter honoured, clear restores 11), `#po-supplier-search-popup-search` (Enter → "No data to display", clear restores, footer untouched) and `#po-invoice-po-picker-search` (opens, single-line, footer intact, icon and Enter paths error-free).

## Remaining unverified items

| Cause | Count | Detail |
| --- | --- | --- |
| Pre-existing database drift | 1 | `/sales/credit-notes` returns 500 `Invalid column name 'CostingRevision'`. `SaCdn.cs` declares the property and is byte-identical to `origin/productionv2`; the local `ERPWeb` database carries the column only on `PoCdn` and `POInvoice`. No `ErpWeb.Model`/`ErpWeb.Core`/`ErpWeb.Data` file changed on this branch, so the failure reproduces identically on the untouched baseline. |
| Prerequisite data absent | 13 | Picker and popup triggers only render once a supplier, customer or source document is chosen; the seeded dataset has no qualifying transaction. |

Per the plan's status policy the PR stays draft until a reviewer clears those 13 rows or accepts the documented environment rationale.

## Residual risks

1. Rendered-DOM checks (`label[for]` to `input#id`, focus ring, icon centering, measured heights) are proven statically and by compilation only.
2. The local-only operator-failure banner (`8d09a22`) is not on this branch, so those screens differ from the local development worktree.
3. The 27 pre-existing test failures remain red and are unrelated to this change.
