# Inventory posting — recovery & retry

Operational notes for BizEERP Inventory PostingEngine (contract v3.2).

## Idempotent post

- Calling `PostAsync` on a document already in `POSTED` status returns the existing result and **does not** insert new `StockLedger` rows.
- Safe for client retries and double-clicks.

## Deadlock handling

```text
maxAttempts = 3   // TOTAL attempts, not “3 retries”

Attempt 1 = initial transaction
Attempt 2 = retry after deadlock
Attempt 3 = retry after deadlock

After 3 failed deadlock attempts → DeadlockRetryExhausted
```

On SQL Server, inventory mutations take `UPDLOCK, HOLDLOCK` on `StockBalance` / `ItemCost` / `LotBalance` in fixed key order (ItemVariant → Warehouse → Location → Lot) to reduce deadlock cycles. Transfer locks lower `WarehouseId` first.

## Failure before COMMIT

- No ledger rows are kept; document stays at prior status.
- Caller may safely call `PostAsync` again.

## Compensating reversal

- Posted documents are not edited.
- `ReverseAsync` creates a compensating document, posts opposite movements, marks the original `REVERSED`.
- Original ledger rows are retained (immutable history).

## Closed periods

- `DocDate` must fall in an **open** `InventoryPeriod`.
- Closed period → `PeriodClosed`.
- Historical balances: use `StockSnapshot` / as-of valuation from ledger — never live `StockBalance`.

## Operational rebuild (admin)

- `IInventoryReconciliationService.RebuildOperationalBalancesAsync` rebuilds `StockBalance`, `LotBalance`, and `ItemCost` from `StockLedger`.
- Requires `CLOSE` on `INV_RECONCILE`. Logged as **AUDIT**.
- Use only after investigating `FindIssuesAsync` results.

## VIEW_COST

- Cost fields on document DTOs, stock balance, stock card, and as-of valuation are omitted/masked unless the user has `VIEW_COST` on Inventory (or is `SYSTEM_ADMIN`).
