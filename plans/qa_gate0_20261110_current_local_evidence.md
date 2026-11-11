# Gate 0 — current local evidence (QA operational completion v4)

**Evidence date:** 2026-11-11  
**Plan reviewed:** `plans/productionv2_QA_SOLID_COMPLETION_AI_AGENT_PLAN_v4_NO_COSTING.md`  
**Work authorized:** Gate 0 audit only. **No Phase 1–5 product code, schema, policy, or Git branch changes.**  
**Git operations:** none (`pull` / `checkout` / `reset` / `stash` / `merge` not used).

---

## Gate 0 status

**`NEEDS_PLAN_RECONCILIATION` + `SQL_SCHEMA_UNVERIFIED`**

Not `PASS_FOR_QA_ONLY_WORK`. Not `STOP_WRONG_CHECKOUT`. Not `HISTORICAL_ON_TENANT_BLOCKED` (cannot prove or disprove historical ON tenants without SQL). Not `CONFLICTING_WORKTREE` for checkout identity, but **uncommitted Sales pricing work is present and must not be mixed into QA**.

**Recommendation:** `BLOCKED` for Phase 1 until this evidence is reviewed. Gate 0 itself is complete as an audit.

---

## 1. Repo identity

| Fact | Value |
|---|---|
| Opened worktree | `C:/wincom/productionv2` |
| Origin | `https://github.com/mokth/net10projectTemplate.git` |
| Current branch | `productionv2` (user-confirmed; not switched) |
| Local HEAD | `e24f54069316a346171fed7f14017c09d593cb95` |
| HEAD subject | Merge pull request #5 from mokth/fix/dataprotection-key-ring (2026-11-07) |
| Plan-pinned / `origin/productionv2` | `584411e317ac6990ed8a5f0886207779d821deaf` |
| Tracking | **behind 14**, ahead 0 |
| Solution | `ErpWeb.slnx` present |
| SDK | `10.0.402` / `net10.0` |
| DevExpress | `DevExpress.Blazor` **26.1.4** (`ErpWeb.csproj`, `ErpWeb.UI.csproj`) |
| Extra worktree | `C:/Users/User/.local/share/kilo/worktree-pool/productionv2-0cfef8a33bda/enchanting-yellowhorn` HEAD `d1e992c` **detached** — reported only, not used |
| Helper script | `qa_productionv2_repo_preflight.ps1` **absent** locally |

### 1.1 Local vs plan-pinned remote

The 14 origin commits not in local HEAD are inventory costing / WAC replay, plus a small additive method on the physical-hold authorizer:

- `AssertRemainingCoversHoldsAsync` added on `IQaPhysicalHoldAuthorization`, `QaPhysicalHoldAuthorizationService`, `DisabledQaPhysicalHoldAuthorization`.

**Local QA source wins for this plan.** Those three remote QA files stay **read-only**. Do **not** pull the 14 costing commits to implement QA.

### 1.2 Dirty / untracked files (protected)

**Do not include any of these in a future QA commit.**

Modified Sales pricing (uncommitted):

- `ErpWeb.Core/Sales/ISaCdnService.cs`, `ISaDoService.cs`, `SaCdnService.cs`, `SaDoService.cs`, `SaInvoiceService.cs`, `SaItemFamilyPricing.cs`, `SaLinePricingResults.cs`, `SaSalesRefService.ItemFamily.PriceSources.cs`
- `ErpWeb.UI/Sales/PriceInquiry.razor[.cs]`, `SaCdn.razor.cs`, `SaDo.razor.cs`, `SaInvoice.razor.cs`, `SaPriceInfoContext.cs`, `SaPriceInfoDialog.razor[.cs,.css]`, `SaPriceInfoPresentation.cs`, `SaQt.razor.cs`, `SaSo.razor.cs`
- `ErpWeb.Tests/Sales/Master/SaCompanyPriceMethodTests.cs`, `SaItemFamilyPricingContractTests.cs`, `SaItemFamilyResolutionServiceTests.cs`
- `ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs`, `SaDoServiceTests.cs`, `SaInvoiceServiceTests.cs`, `SaLinePricingConsumerTests.cs`, `SaPriceInfoPresentationTests.cs`

Untracked Sales:

- `ErpWeb.Core/Sales/SaLinePricingTrigger.cs`
- `ErpWeb.Tests/Sales/Transaction/SaInvoiceFromDeliveryOrderTests.cs`, `SaLinePricingTriggerTests.cs`

Untracked plan markdown (expected; v4 plan is not a codebase mismatch):

- `plans/productionv2_QA_SOLID_COMPLETION_AI_AGENT_PLAN_v4_NO_COSTING.md` and sibling plan files

QA product files under `ErpWeb.Core/Quality`, `ErpWeb.UI/Quality`, `ErpWeb.Tests/Quality`, and `ErpWeb.Model/.../Quality` are **clean** versus HEAD.

---

## 2. Q01–Q18 file and method map (local HEAD)

All 18 findings: **CONFIRMED**. None `ALREADY_FIXED`. None `DIFFERENT_CONTRACT` except Q09 label vs search (implementation matches MR, not all-stock). Tenant/runtime items remain `NOT_VERIFIED` only for SQL/UAT, not for source.

| ID | Status | Path / method / lines | Local evidence |
|---|---|---|---|
| **Q01** | **CONFIRMED** | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs` `OnRequestQa` 749–768; `IvMiscReceipt.razor.cs` `OnRequestQa` 557–566; `QaCaseEntry.razor.cs` `ApplyQueryPrefill` 578–581; `QaSourceResolver.RevalidatePostedOrPileAsync` 306–316; `QaCaseService.SearchPostedInventoryAsync` 1058–1063 | Posted GRN/MR Request QA puts **displayed `BatchNo`** in `sourceHeaderId`. Resolver looks up `IvTrxBatches.Id`. Search rows expose `SourceHeaderId = x.b.Id` and `SourceDocNo = BatchNo`. Prefill copies the query value onto the header **before** lookup; `QaSourcePrefill.Matching` may recover via `SourceDocNo` **or** Id if the batch is in the first search page. Wrong Id persists if prefill matching misses or the user saves the query value. |
| **Q02** | **CONFIRMED** | `QaSourceResolver.RevalidateOutputAsync` 425–427; `RevalidatePostedOrPileAsync` 372–374 | Missing `SourceHeaderId` returns `null` (success) for Daily Production output. Posted GRN with missing id skips the batch lookup (`return null`) unless type is `INVENTORY_STOCK` (pile path). Numeric unknown id for posted batch **does** return `BatchNotFound`. |
| **Q03** | **CONFIRMED** | `RevalidateIncomingAsync` 198–225; `RevalidateLotAsync` 268–290; `RevalidatePostedOrPileAsync` 331–366; `RevalidateOutputAsync` 442+ | Each QA line is compared to the **full** live source qty. No `GroupBy(source line).Sum(Qty)` in `ErpWeb.Core/Quality`. Two case lines on the same source detail can each pass and together exceed the source. |
| **Q04** | **CONFIRMED** | `QaCaseService.Stale` 1555–1558; `QaPolicyService.SaveAsync` 105–109 | Stale is false unless **both** incoming and stored `RowVersion` are non-empty. Empty/missing token skips concurrency. |
| **Q05** | **CONFIRMED** | `SaveInspectionAsync` 473–484; `ApproveAsync` 576–581 | Each inspection save overwrites header `InspectedBy`. Approve compares only that header field when `InspectorApproverSeparation` is on. Per-line `inspection.Inspector` is stored but unused for SoD. |
| **Q06** | **CONFIRMED** | `SaveInspectionAsync` 452–455; `SubmitForReviewAsync` 527–529 | Inspected qty may be `< line.Qty`. Submit for review only requires each line has **an** inspection row. Uninspected remainder is not blocked. Disposition total is validated against **inspected** qty only (`ValidateDispositions` 1317+). |
| **Q07** | **CONFIRMED** | `QaCaseEntry.razor.cs` `CanInspect` 61–64; `QaCaseEntry.razor` 336–343; `SaveInspectionAsync` 400–402; `RejectAsync` 742 | UI Inspect is view-mode + status only (**no Edit**). Server inspect still requires `EDIT`. Reject button is inside `@if (CanApprove)` even though inner auth is `REJECT`. Server `RejectAsync` correctly uses `PermissionCodes.Reject`. |
| **Q08** | **CONFIRMED** | `ApproveAsync` 609–638; `QaLogicalHoldAuthorizationService.PlaceFromApprovedCaseAsync` 29–31; `QaPhysicalHoldAuthorizationService.PlaceFromApprovedCaseAsync` 29–31 | Approve still **places** logical/physical holds when the injected services are present **and** the published policy flag is ON. Incoming/FG/non-good `Issue*Grants` exist at 1429+ but are **not** called from `ApproveAsync`. Historical ON tenants are **SQL_SCHEMA_UNVERIFIED**. Do not change authorizer implementations in later QA-only work. |
| **Q09** | **CONFIRMED** | `SearchSourcesAsync` 868–872; `SearchPostedInventoryAsync` 1019–1034 | `INVENTORY_STOCK` searches posted **Miscellaneous Receipt** lines, not `IvBalLoc` piles. `IvMiscReceipt.OnRequestQa` uses that source type. Relabel as Posted MR (or add a separate read-only pile picker) in a later phase. |
| **Q10** | **CONFIRMED** | `TryLoadSourceLinesAsync` 429–461; `SelectSource` 339–359; `SearchSourcesAsync` take clamp 862 | UI requests `Take = 200`. Server **clamps take to 100**. Each match calls `SelectSource`, which overwrites header fields; last match wins. Exact source is not guaranteed on page 1. |
| **Q11** | **CONFIRMED** | `GetInquiryAsync` 194–273; `QaDestinationComposer.IsFirstPass` 207–210 | Full filtered graph `ToList` (lines, inspections, grants, holds, allocations) **then** Skip/Take. `FirstPassYield` = approved/closed with zero reject qty / cases that reached inspection. Not immutable first-attempt FPY. |
| **Q12** | **CONFIRMED** | `QaDestinationComposer.Compose` 27–36, 96–103; `QaCaseEntry.razor.cs` `PhaseChip` 88–99 | Destinations use `SUGGESTED` / `LINKED_UNVERIFIED` with “Quality does not post stock”. UI chip still says **“Accepted for GRN”** / **“Accepted for FG receipt”** on approved incoming/FG cases. Inquiry banner already warns destinations never post stock. |
| **Q13** | **CONFIRMED** | `QaFoundation.cs` `QaInspection` 101–114; `QaFoundationConfiguration.cs` 167–169; `SaveInspectionAsync` 465–477 | Unique index `UX_QaInspection_CaseLine`. Save overwrites the single inspection row. `QaInspectionAttempt` does not exist in product code (plans only). |
| **Q14** | **CONFIRMED** | `QaCaseEntry.razor` 60; no `QaAction` entity | “Attachments are not enabled in this phase.” Follow-up is unstructured `QaAuditEvent` payload only. |
| **Q15** | **CONFIRMED** | `QaWorkbench.razor` source/status cells; `QaInquiry.razor`; `QaCaseEntry.razor` source combobox; `QaCodes.cs` 5–10 | Grids/filters show raw codes (`INCOMING_PRE_GRN`, `FG_STAGING_LOT`, `DRAFT`, …). QCHOLD appears in eligibility/settings copy. |
| **Q16** | **CONFIRMED** | `QaSchemaReadiness.Classify` 365–371; `QaPolicyService.RejectNewEnablement` 184–210; `QaSettings.razor` 60–74 | All-absent allowlisted tables ⇒ provenance **Unknown**, not “never installed”. Settings cannot newly enable frozen gates. Turn-off blocked when `HasStockObligationsAsync` (224–273) if schema pairs are ready. Tenant obligation counts **not queried**. |
| **Q17** | **CONFIRMED** | `QaCaseService` save validation 1307–1311; `RevalidateIncomingAsync` 173–177 | Incoming allowed with PO **or** delivery note. Blank PO skips PO revalidation (`return null`). Delivery note is not a trusted GRN key. |
| **Q18** | **CONFIRMED** | `ErpWeb.Tests/Quality/` 11 classes; this Gate 0 run | SQLite-heavy. One skipped SQL Server probe. No in-repo QA browser UAT. See §6 for **actual** counts. |

---

## 3. Source identity matrix

| End-user source | Canonical DB identity | Displayed number | Request-QA query today | Revalidation | Limitation |
|---|---|---|---|---|---|
| Incoming / PO before GRN | Company+branch+`PoNo`+`PoRelNo`+`PoLineNo` **or** unlinked delivery note text | PO no / delivery note | `source=INCOMING_PRE_GRN`, `poNo`, `deliveryNote` | PO path if `PoNo` set; unlinked if PO blank | Does not create GRN. Delivery note is not a transaction PK. |
| Posted GRN | `IvTrxBatch.Id` + `IvTrxBatchDetail.Id` + `DocumentRevision` | `BatchNo` | **`sourceHeaderId=BatchNo` (wrong)** | Lookup by **Id**; missing id succeeds | Inspect record only; no quarantine/return. |
| Posted MR (`INVENTORY_STOCK`) | Same batch/detail keys; `TrxType` = miscellaneous receipt | `BatchNo` | **`sourceHeaderId=BatchNo` (wrong)** | Same posted path; pile path only if id missing **and** type is stock | Not all warehouse stock. |
| Warehouse pile (not a search option) | `IvBalLoc` company/branch/item/WH/loc/lot/status | n/a | none | Pile loop only as fallback for `INVENTORY_STOCK` without header id | Read-only observation if enabled later; no hold from this search. |
| Daily Production good / non-good | `ProductionOutput.Uid` + revision | `DocumentNo` | `outputId={Uid}` (correct PK) | Missing `SourceHeaderId` **succeeds** | Non-good counters are not valued lots. |
| WIP lot | `ProductionBalLot.Uid`, stage `PROCESS_WIP` | WO + lot | (no Request QA on WIP page in this pass) | Requires lot id; stage enforced | Hold placement still possible on approve if policy ON. |
| FG staging lot | `ProductionBalLot.Uid`, stage `FG_STAGING` | WO + lot | `lotId={ProductionBalLotId}` (correct) | Stage `FG_STAGING` enforced | Does not post FG. |

**Uniqueness today:** `FreezeGeneration` blocks duplicate PO lines, mixed lots, and mixed posted/output documents **without** a header id. It does **not** aggregate quantities of repeated `SourceLineId` values.

**Stale / deleted / reversed source:** posted batch must be `Posted` and `DeletedAtUtc == null`; canceled PO fails; missing WI/lot/batch returns not-found **when an id is present**. Rollback of a source after inspection is not a dedicated `SOURCE_REVERSED` QA status — it surfaces as revalidation failure or destination `REVERSED` when a sealed allocation exists.

---

## 4. UI → service → EF flow (read-only posting dependencies)

```
Quality UI (QaWorkbench / QaCaseEntry / QaInquiry / QaSettings)
    -> IQaCaseService / IQaPolicyService
        -> QaCaseService / QaPolicyService
            -> QaSourceResolver / QaSourcePrefill / QaDestinationComposer
            -> QaSchemaReadiness (15 allowlisted tables)
            -> EF: QaPolicy, QaCase, QaCaseLine, QaInspection, QaDispositionLine, QaAuditEvent
            -> EF grants/holds (compatibility graph; grants not issued from Approve)
            -> optional IQaLogicalHoldAuthorization / IQaPhysicalHoldAuthorization on Approve
Inventory / Production POST-ROLLBACK (existing, READ-ONLY for this plan)
    -> IQaReceiptAuthorization.ClaimGoodsReceiptAsync (IvInventoryPostingService + replay partials)
    -> IQaFgAuthorization.ClaimFinishedGoodAsync (ProductionFinishedGoodReceiptService.Posting)
    -> IQaLogicalHoldAuthorization / IQaNonGoodAuthorization (ProductionOutputService / ProductionStockWriter)
```

**Menus:** `MenuCodes.QualityWorkbench` = `QA_WORKBENCH`; `QualityInquiry` = `QA_INQUIRY`; `QualitySettings` = `QA_SETTINGS` (`ErpWeb.Core/Menus/MenuCodes.cs` 293–295). Seeded in `scripts/init-qa-menu.sql`: Workbench ACCESS/ADD/EDIT/DELETE/SUBMIT/APPROVE/REJECT/EXPORT; Inquiry ACCESS/EXPORT; Settings ACCESS/EDIT. No POST/ROLLBACK on Quality menus.

**Server permission mapping (current):**

| Action | Permission |
|---|---|
| Search sources / list / get | ACCESS |
| Save draft (new / edit) | ADD / EDIT |
| Submit / submit for review | SUBMIT |
| Start inspection / save inspection / close | EDIT |
| Approve / release hold | APPROVE |
| Reject | REJECT |
| Cancel case / delete draft | DELETE |

No `PermissionCodes.Inspect`.

**CostLayer:** no references under `ErpWeb.Core/Quality`.

---

## 5. Schema / tenant / frozen gates

**Allowlisted tables (15):** `QaPolicy`, `QaCase`, `QaCaseLine`, `QaInspection`, `QaDispositionLine`, `QaAuditEvent`, `QaReceiptGrant`, `QaReceiptAllocation`, `QaFgGrant`, `QaFgAllocation`, `QaLogicalHold`, `QaPhysicalHold`, `QaPhysicalAllocation`, `QaNonGoodGrant`, `QaNonGoodAllocation`.

**SQL scripts present:** `scripts/create-qa-foundation.sql`, `create-qa-receipt-grant.sql`, `create-qa-fg-grant.sql`, `create-qa-logical-hold.sql`, `create-qa-physical-hold.sql`, `create-qa-nongood.sql`, `init-qa-menu.sql`.

**Frozen gates:** `IncomingInspectionEnabled`, `FinalFgInspectionEnabled`, `LogicalHoldEnabled`, `PhysicalHoldEnabled`, `NonGoodEnabled`. `RejectNewEnablement` blocks OFF→ON. `KeepExistingOnly` persists current AND requested. Settings UI is display-only for those flags.

**Tenant matrix:** **not executed.** Customer SQL was not queried (plan forbids it without separate authorization). No isolated test database was named. Therefore:

- Historical ON policies: **unknown**
- Active logical/physical holds and posted allocations: **unknown**
- Per-company schema completeness: **unknown**

Status remains **`SQL_SCHEMA_UNVERIFIED`**. Q08 record-only rollout cannot be certified for any live tenant until a read-only test-DB probe is authorized.

**New QA-only tables requested this gate:** none. `QaInspectionAttempt` / `QaCorrectiveAction` / `QaDocumentLink` / `QaAttachment` stay **not proposed** until a later DBA-approved phase.

---

## 6. Tests actually run (this Gate 0)

**Command:**

```text
dotnet test .\ErpWeb.Tests\ErpWeb.Tests.csproj --filter "FullyQualifiedName~Quality" --logger "trx;LogFileName=qa-gate0.trx" --results-directory ".\TestResults\qa-gate0" --verbosity minimal
```

**Result (2026-11-11, this worktree, local HEAD `e24f540`):**

| Metric | Count |
|---|---|
| Failed | **0** |
| Passed | **90** |
| Skipped | **1** |
| Total | **91** |
| Duration | 28 s |

**Skipped (explicit, not a silent pass):**

- `ErpWeb.Tests.Quality.QaSchemaReadinessTests.SqlServer_probe_enlists_in_the_current_transaction`  
  Skip reason: not executed unless `ConnectionStrings__SqlServerTestConnection` names a scratch database containing `test`.

**Classes present (11, preserved):** `QaCaseQueryBindingTests`, `QaDestinationTests`, `QaFgGrantTests`, `QaFoundationTests`, `QaIncomingGrantTests`, `QaLogicalHoldTests`, `QaNonGoodGrantTests`, `QaPhysicalHoldTests`, `QaSchemaReadinessTests`, `QaSourceGenerationTests`, `QaSourcePrefillTests`.

**Not run / not claimed:** Inventory/Production/Procurement full regression; SQL Server two-session concurrency; browser UAT. A passing SQLite Quality filter is **not** Phase 1 or release evidence.

**Inventory/Production baseline:** not executed in Gate 0 (out of authorized work). Pre-existing costing failures on origin’s 14 commits are **not** this tree.

---

## 7. Proposed later QA-only files vs prohibited baseline

### Allowed to change **after a separate Phase 1 approval** (not this gate)

- `ErpWeb.Core/Quality/QaCaseService.cs`, `IQaCaseService.cs`, `QaSourceResolver.cs`, `QaSourcePrefill.cs`, `QaDtos.cs`, `QaCodes.cs`, `QaDestinationComposer.cs`
- `ErpWeb.UI/Quality/QaCaseEntry.razor[.cs,.css]`, `QaWorkbench.razor[.cs]` (no `.css` in this tree), `QaInquiry.razor[.cs,.css]`, `QaSettings.razor[.cs,.css]`, `QaHowToUsePopup.razor`, `QaRecordOnlyBanner.razor`
- Request-QA **navigation only** on `IvGoodsReceipt.razor.cs`, `IvMiscReceipt.razor.cs` (and production Request QA pages only if identity must stay consistent)
- `ErpWeb.Tests/Quality/*` extensions
- Optional later: `QaPolicyService` concurrency token handling **without** unfreezing gates

### Prohibited (diff must stay zero)

- CostLayer, inventory/production quantity writers, ledger/history, `QCHOLD` status mutation
- POST / ROLLBACK / replay coordinators and call sites
- `QaReceiptAuthorizationService`, `QaFgAuthorizationService`, `QaNonGoodAuthorizationService`, `QaLogicalHoldAuthorizationService`, `QaPhysicalHoldAuthorizationService` and Disabled* twins (including the remote-only `AssertRemainingCoversHoldsAsync`)
- Policy gate new-enablement; turning OFF historical ON flags
- Dirty Sales pricing files listed in §1.2
- New FKs into stock posting tables; customer tenant SQL; Git checkout/reset/merge of costing commits

### Local UI inventory note

`ErpWeb.UI/Quality/` has 16 files. There is **no** `QaWorkbench.razor.css`. Plan references to that file are stale; do not invent it.

---

## 8. Phase 1 readiness (not started)

Confirmed defects that Phase 1 would address after a **new** approval: Q01–Q08, Q17 (and Q10 identity load as it feeds Q01).

**Stop conditions still open:**

1. Uncommitted Sales work in this worktree.
2. Local HEAD behind 14 costing commits — do not pull them for QA.
3. SQL tenant / historical ON policy unknown (Q08).
4. No SQL Server two-session tests yet.
5. No browser UAT yet.

**This Gate 0 does not implement those fixes.**

---

**END — Gate 0 evidence — local `productionv2` HEAD `e24f540` — no costing / no stock-writer changes.**
