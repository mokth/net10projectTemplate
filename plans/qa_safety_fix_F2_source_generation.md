# QA Safety Fix — F2 incoming record-only and source generation

**Gate:** F2 only.  
**Status:** IMPLEMENTED — STOP. F3–F5 not authorized.  
**Recorded:** 2026-11-03, local Windows checkout.  
**HEAD:** `dcb614e` (`fix/dataprotection-key-ring`).  
**Class:** B costing-adjacent. No quantity/cost/FIFO/WAC/ledger algorithm changes. No flag enablement. No live SQL. No GRN `DeliveryInstanceId` column. Dirty `QaSettings.razor(+css)` not overwritten.

## 1. What F2 did

Incoming stays unactivatable:

- No new `QaReceiptGrant` on approve (F1 freeze kept)
- Claim still does not match PO/item/UOM/RefNo
- `QaCase.DeliveryInstanceId` remains a QA-owned Guid; it is never a GRN batch id

Whole-case source identity is frozen (R-21) on SaveDraft, SaveInspection, and Approve:

- One PO release; duplicate / injected PO lines rejected
- Live PO/lot/posted batch/output/pile re-fetched under company+branch
- Forged ids, UOM/item mismatch, cancelled PO, wrong lot stage, and qty above live source fail closed
- Mixed lots on a WIP/FG case fail closed
- FG grants stay unissued (QA-only base snapshot not authorized)

Policy versioning still deferred (R-09). `PolicyVersion` stays 0.

## 2. Files

Added: `ErpWeb.Core/Quality/QaSourceResolver.cs`, `ErpWeb.Tests/Quality/QaSourceGenerationTests.cs`, this evidence file.  
Edited: `QaCaseService` SaveDraft/SaveInspection/Approve, incoming/FG/nongood foundation tests.  
Not edited: plan file, Settings UI, posting math, GRN schema, grant matchers.

## 3. Tests this session

```
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --filter FullyQualifiedName~ErpWeb.Tests.Quality
```

**Passed! Failed 0, Passed 86, Skipped 1, Total 87**, duration 23 s, exit 0. SQLite EnsureCreated.

Skipped: SQL Server catalog-probe enlistment (needs scratch catalog named `test`). **NOT EXECUTED.**

## 4. Still blocked

1. Incoming cannot authorize GRN until a separate GRN delivery-identity design is approved.
2. FG grant UOM/base snapshot not authorized.
3. Hold algebra, ADJ/TR restore, SC/VR lineage remain F3.
4. SQL Server competing FG claims **NOT RUN**.

## 5. GO / NO-GO

| Gate | Result |
|------|--------|
| F2 incoming record-only + source freeze | **PASS** as local SQLite evidence |
| Flag enablement / GRN DeliveryInstanceId / FG grants | **NO-GO** |
| F3–F5 | **NO-GO** — not authorized |

**STOP.**
