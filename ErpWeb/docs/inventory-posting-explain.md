# How `PostInventoryMIAsync` works

This note explains `PostInventoryMIAsync` in `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` in plain language.

The interface `IIvInventoryPostingService` only says “post these batches.” The real work lives in `IvInventoryPostingService`.

**MI** means **Miscellaneous Issue**: a document that takes stock **out** of the warehouse (write-off, sample, consumption, etc.).

**Posting** means: this draft is now official, and on-hand quantity must actually go down.

Think of it as: *“Take this issue document, check it is valid, take the stock off the shelf, write it in the history book, then mark the document POSTED.”*

The method handles **one batch number** at a time. If the user posts several issues, the outer method (`DispatchAsync`) calls this once per batch.

---

## Big picture: all or nothing

The first thing it does is open a **database transaction**.

That is like a sealed envelope:

- If every step succeeds, the envelope is sealed (`Commit`) — stock is reduced and the document is POSTED.
- If anything fails, the envelope is thrown away (`Rollback`) — stock and the document look as if posting never started.

That is why you never get “stock went down but the document is still NEW,” or the opposite.

---

## The objects it works with

| Everyday idea | In this function |
| --- | --- |
| The issue document (header) | `IvTrxBatch` — batch number, status NEW/POSTED, date |
| The lines on the document | `IvTrxBatchDetail` — item, qty, warehouse, lot, **which stock pile** (`FromBalLocId`) |
| One pile of stock on a shelf | `IvBalLoc` — item + warehouse + location + lot + status, with `StdQty` (on hand) |
| Item master (rules for the item) | `IvStockMaster` — is it stock-controlled? lot-controlled? active? |
| Official “this happened” record | `IvTrxHistory` — one row per line after posting |

An issue line does **not** say “take 5 of item ABC from anywhere.” It points at a **specific pile**: `FromBalLocId`. That is the shelf bin we will take from.

---

## Step by step

### 1. Lock the document so nobody else can post it at the same time

```csharp
var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
if (batch is null
    || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase))
{
    return IvInventoryPostingBatchResult.Fail(batchNo, "Miscellaneous issue was not found.");
}

if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
{
    return IvInventoryPostingBatchResult.Fail(batchNo, "Only NEW issues can be posted.");
}
```

It finds the batch for **this company and branch**, and **locks** it. Locking means: if another user is also posting this same document, they wait.

Then it checks:

1. Does this batch exist, and is it really an **MI** (not a receipt or something else)?
2. Is status **NEW**? Only drafts can be posted. Already POSTED or cancelled documents are rejected.

---

### 2. Load the lines and refuse empty / already-posted work

```csharp
var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
if (details.Count == 0)
{
    return IvInventoryPostingBatchResult.Fail(batchNo, "Issue has no lines.");
}

if (await _posting.HistoryExistsForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken))
{
    return IvInventoryPostingBatchResult.Fail(batchNo, "History already exists for this batch.");
}
```

- No lines → nothing to issue → fail.
- History already exists → this batch was already posted (or leftover data) → fail, so we never post twice.

---

### 3. Lock item masters and build a “plan” per line

It loads every item used on the document and builds a **plan** (`MiLinePlan`) for each line via `BuildMiPostLine`.

The plan is a cleaned-up checklist: item code, quantity, which pile (`FromBalLocId`), and a “slice” (item + warehouse + location + lot + status).

`BuildMiPostLine` rejects a line if:

| Check | Why |
| --- | --- |
| Item code missing | We would not know what to take |
| Item not found or inactive | Cannot issue a dead/unknown item |
| Item is **not** stock-controlled | MI is only for items that keep on-hand qty |
| Quantity ≤ 0 | Cannot issue zero or negative |
| No source pile (`FromBalLocId`) | Must know **which** bin to take from |
| No warehouse | Incomplete location |
| No item status | Incomplete stock identity |
| Lot-controlled item but no lot | Lot items must name the lot |
| Non-lot item **has** a lot | Data is inconsistent |

If **any** line fails, the whole batch is rolled back. Partial posting is not allowed: you cannot take 2 of 3 lines and leave the third.

---

### 4. Combine quantities that hit the same pile

```csharp
var deltaById = new Dictionary<int, decimal>();
var sliceById = new Dictionary<int, IvStockSliceKey>();
foreach (var plan in linePlans)
{
    deltaById[plan.FromBalLocId] = deltaById.GetValueOrDefault(plan.FromBalLocId) + plan.Quantity;
    sliceById[plan.FromBalLocId] = plan.FromSlice;
}
```

Example: two lines both take from pile **#42** — 3 pcs and 2 pcs. The function does **not** reduce that pile twice separately first. It adds them: **need 5 from pile #42**.

That way the “do we have enough?” check is against the **total**, not line by line in a way that could look OK then fail later.

---

### 5. Lock each stock pile, in a fixed order

It sorts the piles by their “slice” (item, warehouse, location, lot, status) and locks them one by one.

**Why sort?** If User A locks pile 1 then pile 2, and User B locks pile 2 then pile 1, they can wait forever for each other (deadlock). Same order every time avoids that.

For each locked pile it also checks:

1. The pile still exists for this company/branch.
2. The pile still matches what the issue line says (same item, warehouse, location, lot, status).

If someone changed that pile after the document was saved (moved stock, changed lot, etc.), posting stops instead of taking the wrong stock.

---

### 6. Check quantity **before** taking anything

```csharp
foreach (var (balLocId, required) in deltaById)
{
    var actual = locked[balLocId].StdQty;
    if (required > actual)
    {
        await tx.RollbackAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Fail(
            batchNo,
            $"Insufficient quantity on balance Id {balLocId} (on hand {actual}, required {required}).");
    }
}
```

If you need 10 and the shelf has 7, it **fails the whole batch**. Stock is not reduced at all.

This is the important difference from **receipt (MR)**:

- Receipt **adds** stock (can even create a new pile).
- Issue **must** take from an existing pile, and **must not** go negative.

---

### 7. Actually decrease on-hand quantity

```csharp
foreach (var balLocId in orderedIds)
{
    var required = deltaById[balLocId];
    var affected = await _posting.DecreaseBalLocQtyAsync(
        db, balLocId, companyCode, branchCode, required, batch.TrxDtTime, cancellationToken);
    if (affected != 1)
    {
        await tx.RollbackAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Fail(
            batchNo,
            $"Stock decrease failed for balance Id {balLocId} (insufficient quantity or missing row).");
    }
}
```

The SQL is roughly: *subtract qty, but only if `StdQty >= qty`*.

The extra check is for a race: between “we saw 10 on hand” and “we subtract 8,” another user might have taken stock. Then `affected != 1` and this posting rolls back.

`TestHookAfterMiStockUpdate` is **only for tests**: it can fake a crash after stock is reduced but before history is written, to prove the transaction still rolls everything back.

---

### 8. Write history and stamp the lines

For each line it:

1. Stores on the detail which pile and lot were used (`FromBalLocId`, `FromLotId`).
2. Inserts one **history** row: a frozen copy of “we issued this item, from this warehouse/location/lot, this qty, on this date.”

History is the audit trail. Later, rollback uses these rows to put stock back.

Then it generates an `OperationId` (a unique id for this posting run) so you can tell one post apart from another.

---

### 9. Mark the document POSTED and commit

```csharp
batch.BatchStatus = IvBatchStatuses.Posted;
batch.PostedDate = now;
batch.PostedBy = uid;
batch.PostedCount += 1;
batch.PostingOperationId = opId;
batch.ModifiedDate = now;
batch.ModifiedBy = uid;

await db.SaveChangesAsync(cancellationToken);
await tx.CommitAsync(cancellationToken);
```

Only after stock is reduced **and** history is written does the header become POSTED. Then `SaveChanges` + `Commit` make it permanent.

If that commit succeeds, it returns success with the batch number and operation id.

---

## Mental flowchart

```
User clicks Post on MI batch 1001
        │
        ▼
Open transaction (all or nothing)
        │
        ▼
Lock document ── not MI or not NEW? ──► fail, undo
        │
        ▼
Load lines ── empty or already in history? ──► fail, undo
        │
        ▼
Check each line (item, qty, lot, source pile)
        │ any line bad
        ▼
Combine qty per pile, lock piles in sorted order
        │ pile missing / identity changed / not enough qty
        ▼
Subtract on-hand (SQL: only if still enough)
        │
        ▼
Write history + mark POSTED
        │
        ▼
Commit ── done. Document is official. Stock is lower.
```

---

## How this is different from posting a receipt (`PostInventoryMRAsync`)

| | Miscellaneous **Receipt** (stock in) | Miscellaneous **Issue** (stock out) |
| --- | --- | --- |
| Direction | Add to shelf | Take from shelf |
| Pile | Can **create** a new pile if none exists | Must use an **existing** pile (`FromBalLocId`) |
| Lot | Can create lot records | Uses the lot already on that pile |
| Negative stock | Not the main risk | Main risk — blocked twice (check, then SQL) |
| History fields | `ToWarehouse`, `ToBalLocId`, … | `FrWarehouse`, `FromBalLocId`, … |

---

## What a junior should remember

1. **Posting an issue actually removes stock.** Saving the draft does not.
2. **One batch = one all-or-nothing job.** One bad line fails the whole document.
3. **Lock first, then check, then change.** That stops two users from taking the same last 5 pieces.
4. **History is the official record.** The document status POSTED is the flag; history is what later screens and rollback trust.
5. **Never go negative.** If there is not enough on that exact pile, posting stops.

---

## Source files

| Role | Path |
| --- | --- |
| Public API | `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs` |
| Posting implementation | `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` (`PostInventoryMIAsync`) |
| Line validation | `BuildMiPostLine` in the same file |
| Stock decrease | `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs` (`DecreaseBalLocQtyAsync`) |
| Balance row | `ErpWeb.Model/Entities/Inventory/IvBalLoc.cs` |
| History row | `ErpWeb.Model/Entities/Inventory/IvTrxHistory.cs` |
