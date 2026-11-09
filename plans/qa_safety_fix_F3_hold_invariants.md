# QA Safety Fix — F3 hold and rollback invariants

**Gate:** F3 only.  
**Status:** IMPLEMENTED — STOP before F4/F5.  
**Recorded:** 2026-11-03, local Windows checkout.  
**HEAD:** `dcb614e` (`fix/dataprotection-key-ring`).  
**Class:** B costing-adjacent. No cost, FIFO, WAC, valuation, or ledger formula changes. No policy activation UI. No live SQL. Dirty `QaSettings.razor` and `QaSettings.razor.css` were not overwritten.

## 1. Hold placement

- Physical HOLD + REJECT quantities are aggregated by exact warehouse pile before any hold row is added.
- Each pile is locked in deterministic source order; the aggregate of existing outstanding holds and the requested case must fit current `IvBalLoc.StdQty`.
- Physical hold UOM must equal the pile standard UOM. Hold placement does not alter pile quantity, status, or cost.
- Logical HOLD quantities are converted to `ProductionBalLot.BaseUom`, aggregated with active holds from other cases, and checked against the locked WIP pool.
- Logical hold placement does not alter WIP quantity or value.
- Policy Save remains frozen by F1. These paths are reachable only for an already-persisted ON policy, such as a controlled future deployment.

## 2. Outstanding physical algebra and lineage

Outstanding per hold is:

`max(0, ReservedBaseQty - sum(POSTED allocation Qty))`

Outstanding is summed by pile. REVERSED allocations do not reduce the hold.

- SC and VR have no QA case/hold identity in their current document model. They therefore do not fabricate allocations and can consume only unreserved stock.
- Ordinary ADJ also creates no allocation and can consume only unreserved stock.
- TR is not a quality release and remains blocked while any outstanding hold exists on its source pile.
- QCHOLD stock remains fail-closed while physical hold policy is ON because no explicit QA lineage is carried by these documents.
- Dormant allocation-creation code was removed. Existing historical linked allocations remain readable and reversible.

## 3. Rollback and WIP safety

- ADJ and TR rollback reverse historical POSTED QA allocations inside the same database transaction before stock mutation.
- Replay-aware FIFO, WAC, and Standard transfer rollback roots perform the same restoration.
- A later rollback failure rolls the allocation restoration back with the stock transaction.
- Direct WIP decrements were audited. `ProductionStockWriter` and Production Output paths already check logical holds. Material Issue rollback now blocks before zeroing a `ProductionBalLot` that has an active logical reservation.

## 4. Tests

SQLite `EnsureCreated` evidence:

```text
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Quality"
Passed 89, Skipped 1, Failed 0, Total 90
```

The focused hold classes passed 18/18. Added coverage includes physical and logical ON placement, same-pile atomic over-reservation, logical non-base UOM conversion, posted-versus-reversed outstanding allocation algebra, no implicit SC allocation, and unreserved-stock enforcement.

```text
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build
  --filter "FullyQualifiedName~IvInventoryPostingServiceTests|
            FullyQualifiedName~IvStockTransferLedgerAuthorityTests|
            FullyQualifiedName~ProductionMaterialIssueSequenceOverlapTests"
Passed 277, Skipped 0, Failed 0, Total 277
```

SQL Server transactional/concurrency execution was **NOT RUN**. The existing skipped catalog-probe test still requires a scratch catalog named `test`.

## 5. Boundary

- F3 local SQLite implementation: **PASS**
- SQL Server deployment/concurrency proof: **NOT EXECUTED**
- F4 professional UI: **NO-GO — not authorized**
- F5 disposable SQL UAT/live rollout: **NO-GO — not authorized**

**STOP before F4/F5.**
