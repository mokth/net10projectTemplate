 # Stock Transfer Implementation Plan --- ErpWeb .NET 10

> **Do not implement this document.** It targets a v3.2 architecture
> (`InventoryDocument`, `StockLedger`, `StockBalance`, `ItemCost`) that
> does not exist in this repository.
>
> Use **[stock_transfer_implementation_plan_v8.md](stock_transfer_implementation_plan_v8.md)** instead.

## 1. Purpose

Implement Stock Transfer (`ST`) in the existing
`mokth/net10projectTemplate` ErpWeb architecture.

Target stack:

-   .NET 10
-   ASP.NET Core
-   EF Core
-   SQL Server
-   Blazor Server
-   DevExpress

This plan is based on the repository's locked
`inventory_new_design_v3.2.md` architecture.

## 2. Critical Architecture Rule

Do NOT create a second inventory architecture such as:

-   `StockTransferSource`
-   `StockTransferDestination`
-   a separate transfer posting engine

Use the existing locked architecture:

``` text
InventoryDocument
        |
        v
InventoryDocumentLine
        |
        v
StockMovementAllocation
        |
        v
StockLedger  <-- immutable historical truth
   |       |
   v       v
StockBalance  ItemCost
                  |
                  v
              LotBalance (Phase 3)
```

The Stock Transfer is an `InventoryDocument` with `DocumentType = ST`.

## 3. Business Requirements

The ST function must support:

### 3.1 Same UOM transfer

``` text
WH-A / A01
10 BOX
   |
   v
WH-B / B01
10 BOX
```

### 3.2 Location transfer

``` text
WH-A / A01
20 PCS
   |
   v
WH-A / A02
20 PCS
```

### 3.3 Warehouse + location transfer

``` text
WH-A / A01
   |
   v
WH-B / B05
```

### 3.4 UOM conversion

Example:

``` text
1 BOX = 24 PCS

Source:
WH-A / A01
1 BOX

Destination:
WH-B / B01
24 PCS
```

The posting engine must use the conversion rate captured at posting
time.

### 3.5 One source to multiple destinations

Example:

``` text
Source:
WH-A / A01
1 BOX
1 BOX = 24 PCS

Destination:
WH-A / A02    10 PCS
WH-B / B01     8 PCS
WH-B / B02     6 PCS

Total = 24 PCS
```

The source must generate only one total OUT movement:

``` text
OUT = -24 PCS
```

and destination IN movements:

``` text
IN = +10 PCS
IN = +8 PCS
IN = +6 PCS
```

Do NOT create three separate `-24 PCS` OUT transactions.

## 4. Existing Inventory Contract

The repository's v3.2 contract locks these principles:

1.  `InventoryDocument` is the business transaction.
2.  `InventoryDocumentLine` is the requested movement.
3.  `StockMovementAllocation` is the physical/cost allocation bridge.
4.  `StockLedger` is immutable historical truth.
5.  `StockBalance` is the current operational quantity cache.
6.  `ItemCost` stores warehouse-level Moving Average cost state.
7.  Posted ledger rows are never updated or deleted.
8.  Corrections use compensating reversal.
9.  Negative stock is prohibited.
10. Transfer OUT and IN are atomic.
11. Transfer preserves source cost.
12. Posting is idempotent.
13. Closed inventory periods block normal posting.
14. V1 costing is Moving Average only.
15. Cross-branch transfer is deferred.

## 5. Scope Model

Stock Transfer V1 is same-branch only.

``` text
Company
  |
  +-- Branch
       |
       +-- Warehouse
             |
             +-- WarehouseLocation
```

The service must obtain CompanyId and BranchId from `ICompanyContext`.

Never trust CompanyId or BranchId supplied by the Blazor client.

`ICurrentUserService.LocationCode` is a legacy user claim and must NOT
be treated as an inventory warehouse location.

## 6. InventoryDocumentLine Design

Inspect the current v3.2 `InventoryDocumentLine` implementation first.

**Do not assume that the current entity already has the ST shape described
below.** The actual repository schema is authoritative.

For the target ST model, the following are **logical responsibilities only,
not a migration specification**:

The actual repository entities, field names, relationships, indexes, and
v3.2 contract are authoritative. The AI agent must NOT add these fields merely
because they appear in this plan. Add/change schema only after Gate A/B proves
a real gap.

```text
InventoryDocumentLine = SOURCE movement

ItemVariantId
SourceWarehouseId
SourceLocationId
SourceLotId       (Phase 3)
SourceUOMId
SourceQty
SourceQtyInBase
SourceConversionRateUsed
```

Destination fields such as:

```text
TargetWarehouseId
TargetLocationId
TargetLotId
TargetUOMId
TargetQty
TargetQtyInBase
TargetConversionRateUsed
```

belong conceptually to `StockMovementAllocation`, because one source line
must be able to distribute its quantity to multiple destinations.

Example:

```text
InventoryDocumentLine #1
Source = WH-A/A01
Qty = 1 BOX
BaseQty = 24 PCS
        |
        +-- Allocation #1 -> WH-A/A02 10 PCS
        +-- Allocation #2 -> WH-B/B01  8 PCS
        +-- Allocation #3 -> WH-B/B02  6 PCS
```

If the existing v3.2 schema uses different field names or stores these values
at another level, reuse the existing design rather than creating duplicate
fields.

**Do not add TargetWarehouseId/TargetLocationId to InventoryDocumentLine
merely to make ST work.** First determine whether the existing
`StockMovementAllocation` already provides the required destination model.

Do not create a new `StockTransfer` table unless the implementation-gate
review proves that the locked inventory contract cannot support ST.

## 7. Physical Movement vs Allocation

The implementation must distinguish three concepts:

```text
SOURCE MOVEMENT
    What leaves source stock?

DESTINATION MOVEMENT
    What enters destination stock?

ALLOCATION
    What relationship connects source quantity/value
    to one or more destination movements?
```

Example:

```text
Source Movement
WH-A/A01
-24 PCS
       |
       +---- Allocation #1 ----> IN WH-A/A02 +10 PCS
       +---- Allocation #2 ----> IN WH-B/B01  +8 PCS
       +---- Allocation #3 ----> IN WH-B/B02  +6 PCS
```

`StockMovementAllocation` must NOT be assumed to mean "destination row"
until the actual v3.2 implementation is inspected.

The coding agent must determine the existing business meaning of
`StockMovementAllocation` from:

- entity definition
- EF configuration
- posting service
- queries/reports
- existing tests
- existing stock transactions

If it is currently a cost-allocation mechanism, the agent must not overload
its meaning for Stock Transfer without an approved design change.

## 8. Source and Destination Model

The logical ST structure is:

```text
InventoryDocument
    |
    +-- InventoryDocumentLine
          |
          +-- SOURCE
          |     Warehouse
          |     Location
          |     Lot
          |     UOM
          |     Qty
          |     BaseQty
          |
          +-- StockMovementAllocation
                |
                +-- DESTINATION #1
                |     Warehouse
                |     Location
                |     Lot
                |     UOM
                |     Qty
                |     BaseQty
                |
                +-- DESTINATION #2
                |
                +-- DESTINATION #3
```

The source line answers:

> What inventory is being taken from stock?

The allocation answers:

> How is that source quantity distributed to destinations?

For:

```text
Source = 24 PCS

Allocation A = 10 PCS
Allocation B = 8 PCS
Allocation C = 6 PCS
```

the source quantity is consumed once:

```text
OUT = -24 PCS

IN = +10 PCS
IN = +8 PCS
IN = +6 PCS
```

### Mandatory ledger/allocation relationship gate

The existing v3.2 `StockLedger` ↔ `StockMovementAllocation` cardinality must
be inspected before implementation.

The coding agent MUST document the actual relationship found in the
repository, for example whether:

```text
StockMovementAllocation -> SourceLedgerId / DestinationLedgerId
```

or:

```text
StockLedger -> StockMovementAllocationId
```

or another existing relationship is used.

The agent must then prove how this relationship represents:

```text
1 source line
    -> many destination allocations
    -> 1 source OUT movement
    -> many destination IN movements
```

without violating the locked v3.2 contract.

If the current schema cannot represent this correctly, STOP.

Do not silently change allocation cardinality, add duplicate bridge tables,
or invent a second posting model.

Do not introduce parallel transfer-specific ledger tables.


## 9. Phase 2 --- Non-Lot Stock Transfer

Phase 2 supports non-batch items only.

Allowed:

-   Warehouse to warehouse
-   Location to location
-   Same warehouse, different locations
-   Different warehouses in the same branch
-   Same UOM
-   UOM conversion
-   One source to multiple destinations

Rejected:

-   Batch/lot controlled items
-   LotNo/LotId supplied on a Phase 2 transfer

### Phase 2 allocation semantics

For a transfer with one source and multiple destinations:

```text
Source Line #1
    |
    +-- Allocation #1 -> Destination A
    +-- Allocation #2 -> Destination B
    +-- Allocation #3 -> Destination C
```

The source quantity is consumed exactly once.

Example:

```text
Source = 24 PCS

Destination A = 10
Destination B = 8
Destination C = 6

Total IN = 24
Total OUT = 24
```

The implementation must not multiply the source OUT quantity by the number
of destinations.

The existing Phase-2 1:1 `StockLedger` -> `StockMovementAllocation` contract
must be preserved. Therefore, before coding, inspect the actual v3.2
allocation schema and implement the ST allocation/ledger relationship using
the existing contract rather than inventing a second relationship.

If the existing allocation table cannot represent multiple destinations
for one source line while preserving the v3.2 1:1 ledger-allocation rule,
the schema/contract must be explicitly revised before implementation.

This is a hard design gate. The AI agent must NOT silently alter the
allocation cardinality.

## 10. Phase 3 --- Lot-Aware Transfer

Phase 3 introduces batch-controlled items.

For a normal warehouse/location transfer:

``` text
Source LotId = X
Destination LotId = X
```

The same Lot identity is preserved.

Do not create a new Lot merely because the warehouse or location
changes.

Example:

``` text
LOT001
WH-A/A01
24 PCS

        ST

LOT001 / WH-A/A02   10 PCS
LOT001 / WH-B/B01    8 PCS
LOT001 / WH-B/B02    6 PCS
```

Update both:

-   `StockBalance`
-   `LotBalance`

and maintain the reconciliation:

``` text
SUM(LotBalance.QtyOnHand)
=
StockBalance.QtyOnHand
```

for the corresponding item/warehouse/location scope.

## 11. Important Lot-Split Requirement

The business requirement also describes:

``` text
1 BOX
  |
  +-- 10 PCS -> LOT-A
  +--  8 PCS -> LOT-B
  +--  6 PCS -> LOT-C
```

This is NOT a normal transfer because the LotId changes.

It is a lot transformation/repack operation.

Do not silently implement this as a normal ST rule.

Recommended design:

``` text
ST = Stock Transfer
    Same lot identity
    Warehouse/location movement
    UOM conversion

RS = Repack / Stock Split
    Source package/lot
       |
       +-- Destination lot A
       +-- Destination lot B
       +-- Destination lot C
```

If the business requires lot splitting to happen inside ST, update the
locked Phase 3 contract explicitly before coding. The AI coding agent
must not invent a new lot-transformation rule.

## 12. UOM Conversion

Use the existing UOM conversion architecture.

Conversion direction:

``` text
ToQty = FromQty * ConversionRate
```

Example:

``` text
1 BOX = 24 PCS

FromUOM = BOX
ToUOM   = PCS
Rate    = 24
```

At posting, capture:

``` text
ConversionRateUsed
QtyInBase
```

Historical posted quantities must never depend on today's UOM conversion
table.

UOM conversion changes the transaction/display UOM; it does not create or
destroy physical inventory.

Example:

```text
Before:
WH-A = 1 BOX = 24 PCS

Transfer:
1 BOX -> 24 PCS

After:
WH-A = 0 BOX / 0 PCS
WH-B = 24 PCS
```

The invariant is:

```text
Source Base Qty == Destination Base Qty
```

The physical base quantity is conserved.

All source and destination quantities must be normalized to base UOM
before balance validation.

## 13. Quantity Validation

Example:

``` text
Source:
1 BOX
= 24 PCS base quantity

Destination:
10 PCS
8 PCS
6 PCS

Destination total:
24 PCS
```

Require:

``` text
SourceBaseQty == DestinationBaseQty
```

If:

``` text
Source = 24 PCS
Destination = 23 PCS
```

reject the document.

If:

``` text
Source = 24 PCS
Destination = 25 PCS
```

reject the document.

The UI should display:

``` text
Source Base Qty:       24 PCS
Destination Base Qty:  24 PCS
Remaining:              0 PCS
Status:                Balanced
```

## 14. Document and Quantity Integrity Rules

### 13.1 Document numbering

Use the existing ERP document-number generation mechanism.

The UI must NOT generate the Stock Transfer document number.

Document numbering must be transaction-safe and must follow existing
company/branch/document-type conventions.

### 13.2 Document date vs posting date

Use the existing inventory document date/posting-date semantics.

The posting service is responsible for validating the effective inventory
date against the inventory period.

Do not allow the UI to bypass closed-period validation.

### 13.3 Duplicate source inventory rows

If multiple source rows resolve to the same complete source inventory key:

```text
ItemVariant
Warehouse
Location
Lot
UOM
```

the calculation layer may normalize/merge them when safe.

The merged base quantity must equal the sum of the original quantities.

Do not merge rows when doing so would lose required lot, UOM, cost, or audit
meaning.

### 13.4 Conversion and rounding

Perform quantity conversion using the existing UOM precision and rounding
rules.

Validate the final rounded base quantity.

For multiple destinations:

```text
SUM(Rounded Destination BaseQty)
=
Rounded Source BaseQty
```

must hold exactly according to the existing inventory precision rules.

Never silently discard a rounding remainder.

### 13.5 No partial destination posting

A Stock Transfer is atomic.

If any source or destination line fails validation, the entire document must
remain unposted.

No subset of destinations may be posted successfully.

## 15. Zero and Negative Quantity

Reject:

-   zero source quantity
-   negative source quantity
-   zero destination quantity
-   negative destination quantity

Apply the existing UOM decimal precision and rounding rules from v3.2.

## 16. Quantity Direction Rules

All quantities entered in the ST UI are positive.

Direction is represented by the document structure:

```text
Source -> Destination
```

Never allow:

```text
Source Qty = -10
Destination Qty = +10
```

or:

```text
Source Qty = +10
Destination Qty = -10
```

The posting engine itself determines:

```text
Source ledger = OUT
Destination ledger = IN
```

The user never controls the ledger sign.

## 17. Warehouse and Location Validation

For every source and destination:

1.  Warehouse exists.
2.  Warehouse is active.
3.  Warehouse belongs to current Company and Branch.
4.  Location exists when specified.
5.  Location is active.
6.  Location belongs to the selected Warehouse.
7.  Location belongs to the current Branch.

If LocationId is omitted, resolve the warehouse's automatic `MAIN`
location.

Warehouse creation must create `MAIN` automatically in the same database
transaction, as required by v3.2.

## 18. Allocation Calculation Before Posting

Before database posting, calculate a deterministic transfer allocation
result in memory.

Example:

```text
Source:
1 BOX
Conversion:
1 BOX = 24 PCS

Destination:
10 PCS
8 PCS
6 PCS

Calculated result:
Source Base Qty       = 24
Destination Base Qty  = 24
Remaining             = 0
Balanced              = true
```

The calculation layer must produce:

```text
TransferAllocationResult
    SourceLines
    DestinationAllocations
    TotalSourceBaseQty
    TotalDestinationBaseQty
    RemainingBaseQty
    IsBalanced
```

Use the project's existing model/service conventions where possible; do not
create a new architectural layer merely for this DTO if an equivalent
existing pattern is already present.

Posting must consume the validated allocation result. It must not recalculate
business quantities differently from the UI.

## 19. Posting Algorithm

Implement or extend the existing inventory posting service with:

``` csharp
PostStockTransferAsync(long documentId)
```

Do not create a separate stock-balance manipulation service just for ST.

Conceptual flow:

``` text
BEGIN TRANSACTION

1. Load InventoryDocument
2. Verify DocumentType = ST
3. Verify current Company/Branch scope
4. Verify document is Draft
5. Verify inventory period is open
6. Validate all lines and destinations
7. Resolve MAIN locations
8. Resolve and snapshot UOM conversions
9. Calculate normalized base quantities
10. Build deterministic source/destination allocations
11. Determine ALL affected StockBalance keys
12. Sort affected stock keys in the existing deterministic lock order
13. Begin/continue the database transaction and lock affected balances
14. Re-read source quantities after locking
15. Reject insufficient stock
16. Revalidate ALL source/destination quantity conservation
17. Revalidate that no source/destination inventory key is invalid
18. Revalidate no source/destination quantity is non-positive
17. Create source OUT StockLedger row(s)
18. Create destination IN StockLedger row(s)
19. Create the required StockMovementAllocation rows and ledger links
20. Update StockBalance
21. Phase 3: update LotBalance
22. Apply source cost to destination using existing MAV rules
23. Update ItemCost only when required by the transfer
24. Mark InventoryDocument Posted
25. Store posting/idempotency information
26. COMMIT

On error:
ROLLBACK
```

## 20. Posting-Time Stock Authority

The quantity displayed in the Blazor UI is informational only.

Example:

```text
10:00
UI shows Available = 100 PCS

10:05
Another user posts -80 PCS

10:06
This user posts a 50 PCS transfer
```

The 50 PCS transfer must be validated against the current locked
`StockBalance`, not the UI's earlier value of 100 PCS.

Therefore:

```text
UI AvailableQty
    = display/validation aid only

Posting AvailableQty
    = authoritative current StockBalance
      read after the required row locks are acquired
```

Never trust a client-supplied AvailableQty.

## 21. Concurrency

Stock availability must NOT be checked only when the user opens the page
or clicks Post.

The authoritative check must happen inside the posting transaction.

Example race:

``` text
Available = 15 PCS

User A transfers 10
User B transfers 8
```

Both may initially see 15.

The posting engine must serialize/lock the affected stock records so
that only a valid transaction succeeds.

Use the locking strategy already specified by
`inventory_new_design_v3.2.md`.

Follow the existing lock order consistently.

Deadlock handling:

``` text
maxAttempts = 3
```

This means 3 TOTAL attempts:

``` text
Attempt 1 = initial
Attempt 2 = retry
Attempt 3 = retry
```

Do not describe this as "3 retries".

## 22. Idempotency

Double clicking Post must not create duplicate stock movements.

Posting must detect an already-posted document and return the existing
posting result instead of inserting duplicate ledger rows.

Example:

``` text
User clicks Post
    |
    +-- Request 1 -> posts ST001
    |
    +-- Request 2 -> sees ST001 already posted
                       no duplicate posting
```

## 23. StockLedger Result

Simple transfer:

``` text
ST001

OUT
Item ABC
WH-A / A01
-10 PCS

IN
Item ABC
WH-B / B01
+10 PCS
```

Split destination:

``` text
ST001

OUT
Item ABC
WH-A / A01
-24 PCS

IN
Item ABC
WH-A / A02
+10 PCS

IN
Item ABC
WH-B / B01
+8 PCS

IN
Item ABC
WH-B / B02
+6 PCS
```

Do not modify posted ledger rows later.

## 24. Costing

V1 uses Moving Average.

`ItemCost` grain is:

``` text
Company
Branch
Warehouse
ItemVariant
```

It is NOT location-level.

Therefore:

``` text
WH-A/A01
WH-A/A02
WH-A/A03
```

share the same WH-A + Item cost state.

Do not store duplicated quantity on `ItemCost`.

Do not store duplicated AverageCost on `StockBalance`.

Transfer preserves source cost.

For example:

``` text
Source WH-A cost = RM10/PCS

Transfer:
10 PCS

Destination receives:
10 PCS @ RM10 source cost
```

If the source and destination are in the SAME warehouse, no warehouse-level
`ItemCost` movement/recalculation is required.

If the destination is a DIFFERENT warehouse, use the existing destination
MAV algorithm:

```text
Destination old Qty   = DQty
Destination old Cost  = DCost
Transferred Qty       = TQty
Transferred Unit Cost = SourceCost

New Qty   = DQty + TQty
New Value = (DQty * DCost) + (TQty * SourceCost)
New MAV   = New Value / New Qty
```

For multiple destinations, transferred value must be conserved.

Example:

```text
Source:
24 PCS @ RM10 = RM240

Destination:
WH-A/A02  10 PCS = RM100
WH-B/B01   8 PCS = RM80
WH-B/B02   6 PCS = RM60

Total destination value = RM240
```

For each destination warehouse, the existing warehouse-level MAV algorithm
is applied independently. Same-warehouse location transfers do not create a
new warehouse-level cost movement.

Do not invent a second costing algorithm for ST.

## 25. Cost Rounding and Value Conservation

For a transfer with multiple destinations, calculate transferred value from
the source quantity and source unit cost using the existing inventory
decimal/currency precision.

Example:

```text
Source:
24 PCS @ RM10.00 = RM240.00

Destinations:
10 PCS = RM100.00
8 PCS  = RM80.00
6 PCS  = RM60.00

Total = RM240.00
```

If the existing costing precision causes a rounding residual, apply the
existing ERP residual/rounding policy to one deterministic destination row.

Do NOT silently lose or create value.

Required invariant:

```text
Source Transfer Value
=
Sum of Destination Transfer Values
```

within the existing currency precision rules.

The same deterministic rule must be used on reversal so that the original
inventory value is restored correctly.
## 26. Blazor UI

Create the Stock Transfer page using the existing ErpWeb.UI and
DevExpress patterns.

Recommended layout:

``` text
STOCK TRANSFER
------------------------------------------------
Document No: ST-000001
Date:        29/08/2026

SOURCE
------------------------------------------------
Item       Warehouse  Location  UOM   Qty
ABC-001    WH-A       A01       BOX   1

Base Qty: 24 PCS

DESTINATION
------------------------------------------------
Warehouse  Location  UOM   Qty   Base Qty
WH-A       A02       PCS   10       10
WH-B       B01       PCS    8        8
WH-B       B02       PCS    6        6

Source Base Qty:       24 PCS
Destination Base Qty:  24 PCS
Remaining:              0 PCS

Status: BALANCED

Remarks:
------------------------------------------------

[Save Draft]                         [Post]
```

Use two logical DevExpress grids:

-   Source grid
-   Destination grid

Do not make a single giant grid that mixes unrelated source/destination
concerns.

## 27. Destination Entry

When the source is selected:

1.  Determine source available quantity.
2.  Determine source UOM.
3.  Calculate base quantity.
4.  Allow the user to add one or more destinations.
5.  Each destination can select:
    -   Warehouse
    -   Location
    -   UOM
    -   Quantity
6.  Convert destination quantity to base UOM.
7.  Continuously calculate remaining quantity.

Example:

``` text
Source = 24 PCS

Destination 1 = 10 PCS
Remaining = 14

Destination 2 = 8 PCS
Remaining = 6

Destination 3 = 6 PCS
Remaining = 0
```

Prevent posting when Remaining != 0.

## 28. Document Status

Follow the existing inventory document status machine.

Minimum:

``` text
Draft
  |
  v
Posted
  |
  v
Reversed
```

Posted documents cannot be edited.

## 29. Reversal

Do not UPDATE or DELETE posted inventory ledger rows.

Reverse with a compensating inventory document.

Example:

``` text
Original:
WH-A -> WH-B
10 PCS

Reversal:
WH-B -> WH-A
10 PCS
```

The original remains immutable.

For a multi-destination transfer:

```text
Original:

WH-A/A01
24 PCS
   |
   +-- WH-A/A02 10
   +-- WH-B/B01  8
   +-- WH-B/B02  6
```

the reversal must preserve the allocation mapping:

```text
WH-A/A02 10 -> WH-A/A01
WH-B/B01  8 -> WH-A/A01
WH-B/B02  6 -> WH-A/A01
```

Do not collapse the reversal into an arbitrary single 24 PCS movement if
doing so loses the original source/destination allocation history.

## 30. Audit and Posting Identity

Use the existing `InventoryDocument` audit/posting fields and conventions.

At minimum, a successful post must preserve:

```text
CreatedBy
CreatedDate
PostedBy
PostedDate
```

If reversal is supported by existing document conventions, preserve:

```text
ReversedBy
ReversedDate
ReversalDocumentId
ReversalReason
```

Do not create a separate audit framework for Stock Transfer.

The posting identity must be written as part of the same transaction as the
inventory mutation.

## 31. Permissions

Integrate with existing ErpWeb RBAC.

Recommended permissions:

``` text
VIEW
CREATE
EDIT
POST
REVERSE
VIEW_COST
```

Cost information must remain protected by the Core layer's `VIEW_COST`
permission.

Do not implement a new permission framework.

## 32. Database / EF Changes

Before coding, inspect the existing v3.2 entity and configuration
classes.

Only add missing ST-specific fields/indexes.

Expected areas:

``` text
ErpWeb.Model
    Entities/
    Configurations/
    AppDbContext

ErpWeb.Core
    Inventory/
    Services/
    Posting/

ErpWeb.UI
    Inventory/
    StockTransfer/

ErpWeb.Tests
    Inventory/
    StockTransfer/
```

Use:

-   SQL scripts for database deployment
-   EF Fluent configuration for mappings
-   existing repository/service conventions

Do not redesign the existing DbContext architecture.

## 33. Indexes

Verify the v3.2 indexes exist before adding new ones.

ST lookups will require efficient access to:

``` text
CompanyId
BranchId
ItemVariantId
WarehouseId
LocationId
LotId
DocumentId
DocumentType
Status
```

StockBalance concurrency queries must use the existing unique stock
grain:

``` text
Company + Branch + Warehouse + Location + ItemVariant
```

Do not introduce a second stock-balance grain.

## 34. Acceptance Tests

### Basic transfer

-   [ ] Warehouse A -\> Warehouse B
-   [ ] Same warehouse, Location A -\> Location B
-   [ ] Warehouse A/Location A -\> Warehouse B/Location B
-   [ ] Same UOM
-   [ ] Different UOM

### UOM

-   [ ] BOX -\> PCS
-   [ ] PCS -\> BOX
-   [ ] Decimal conversion
-   [ ] Conversion rounding
-   [ ] Rounded source/destination base quantities reconcile exactly
-   [ ] No rounding remainder is silently lost
-   [ ] Historical conversion rate captured

### Multiple destinations

-   [ ] 1 source -\> 2 destinations
-   [ ] 1 source -\> 3 destinations
-   [ ] Different warehouses
-   [ ] Different locations
-   [ ] Destination total exactly equals source base quantity

### Validation

-   [ ] Same source and destination rejected
-   [ ] Destination too low rejected
-   [ ] Destination too high rejected
-   [ ] Zero quantity rejected
-   [ ] Negative quantity rejected
-   [ ] Insufficient stock rejected
-   [ ] Inactive warehouse rejected
-   [ ] Invalid location rejected
-   [ ] Location belonging to another warehouse rejected
-   [ ] Cross-branch transfer rejected

### Posting

-   [ ] Draft does not affect inventory
-   [ ] Draft does not reserve inventory
-   [ ] Draft can be cancelled without inventory mutation
-   [ ] UI cannot directly mutate StockBalance
-   [ ] OUT + IN are atomic
-   [ ] StockBalance updated correctly
-   [ ] StockLedger rows immutable
-   [ ] Allocation rows correct
-   [ ] Source OUT is not multiplied by destination count
-   [ ] One source line can represent multiple destination allocations
-   [ ] Existing ledger/allocation cardinality is preserved
-   [ ] Ledger/allocation links are traceable from document to source/destination
-   [ ] Source BaseQty equals Destination BaseQty
-   [ ] Quantity conservation invariant passes
-   [ ] Inventory value conservation passes
-   [ ] Total source transferred value equals total destination transferred value
-   [ ] Same-warehouse transfer does not incorrectly change warehouse MAV
-   [ ] Cross-warehouse transfer updates destination MAV correctly
-   [ ] Posted document cannot be edited
-   [ ] Double-post is idempotent
-   [ ] Rollback leaves no partial stock update
-   [ ] One invalid destination prevents the entire document from posting
-   [ ] Reversal preserves original allocation mapping
-   [ ] Reversal restores quantity and value invariants

### Concurrency

-   [ ] Two users consume the same source stock
-   [ ] UI stale AvailableQty cannot bypass posting-time balance validation
-   [ ] Posting re-reads locked StockBalance
-   [ ] All source and destination StockBalance keys are locked
-   [ ] Lock order is deterministic for multi-destination transfers
-   [ ] Only valid quantity can post
-   [ ] Deadlock handling uses 3 total attempts
-   [ ] No duplicate ledger rows after retry

### Phase 3

-   [ ] Batch item requires LotId
-   [ ] Normal transfer preserves LotId
-   [ ] LotBalance updated
-   [ ] LotBalance reconciles with StockBalance
-   [ ] New Lot is not created for a normal transfer

## 35. Special Lot-Split Acceptance Tests

These must NOT be implemented as ordinary ST unless the Phase 3 contract
is explicitly updated.

Example:

``` text
1 BOX
LOT001
= 24 PCS

10 PCS -> LOT-A
8 PCS  -> LOT-B
6 PCS  -> LOT-C
```

Required business decision:

``` text
Option A:
ST does not support lot identity changes.
Use a separate Repack/Split document.

Option B:
Extend Phase 3 ST contract to explicitly support
lot transformation/split.
```

Recommended: Option A.

## 36. Implementation Order

### Phase ST-1 --- Analysis

Inspect the actual v3.2 implementation:

-   InventoryDocument
-   InventoryDocumentLine
-   StockMovementAllocation
-   StockLedger
-   StockBalance
-   ItemCost
-   UOMConversion
-   Warehouse
-   WarehouseLocation
-   existing posting service

Do not code until the existing implementation is mapped.

### Phase ST-2 --- Data model

Add only missing ST fields/configuration.

Run EF/database validation.

### Phase ST-3 --- Allocation calculation

Implement the deterministic source/destination calculation first.

The calculation must prove:

```text
Source Base Qty
=
Destination Base Qty
```

before any database mutation occurs.

Example:

```text
1 BOX = 24 PCS

10 PCS + 8 PCS + 6 PCS
= 24 PCS

Balanced = true
```

Do not update StockBalance, StockLedger, ItemCost, or LotBalance in this phase.

### Phase ST-4 --- Core validation

Implement:

``` csharp
ValidateStockTransferAsync(...)
```

Validation must cover:

-   scope
-   item
-   UOM
-   source
-   destination
-   quantity
-   balance
-   period
-   batch/lot rules

### Phase ST-5 --- Posting

Implement:

``` csharp
PostStockTransferAsync(...)
```

using the existing inventory posting infrastructure.

### Phase ST-6 --- UI

Implement the DevExpress Blazor Stock Transfer page.

### Phase ST-7 --- Tests

Implement unit/integration/concurrency tests.

### Phase ST-8 --- Human review

Stop after the phase and review the result before proceeding.

Do not allow the AI coding agent to implement the entire module in one
pass.

## 37. AI Coding Agent Instructions

The coding agent MUST:

1.  Read `inventory_new_design_v3.2.md` before changing inventory code.
2.  Inspect the existing implementation before creating new classes.
3.  Reuse the locked inventory architecture.
4.  Implement only the current phase.
5.  Preserve existing naming and project conventions.
6.  Use `ICompanyContext`.
7.  Never trust client CompanyId/BranchId.
8.  Never modify posted StockLedger rows.
9.  Never directly manipulate stock from UI code.
10. Perform stock validation inside the posting transaction.
11. Preserve atomic OUT + IN.
12. Preserve source cost.
13. Enforce idempotent posting.
14. Use 3 TOTAL deadlock attempts.
15. Keep Phase 2 non-lot.
16. Do not implement lot transformation unless the contract is
    explicitly changed.
17. Do not implement FIFO, serial, reservation, GL, outbox, landed cost,
    cross-branch transfer, or other deferred features.
18. Do not redesign locked architecture.
19. Stop after each implementation phase for human review.
20. Use the existing document-number generation mechanism.
21. Use existing document audit/posting identity fields.
22. Never generate document numbers in the Blazor UI.
23. Never allow one failed destination to partially post the document.
24. Preserve quantity and value conservation invariants.
25. Preserve the exact source-to-destination allocation mapping for reversal.
26. Treat conceptual fields in this plan as logical responsibilities, not automatic migrations.
27. Do not change global transaction isolation settings for ST without human approval.
28. Validate batch availability at LotBalance level when a lot is selected.
29. Reject unsupported serial-controlled items rather than treating them as lots.
30. Produce and pass the Stock Transfer Posting Proof before implementation.
31. Never directly update StockBalance/ItemCost/LotBalance from ST UI or orchestration code.
32. Do not reserve stock for Draft ST unless a separate approved reservation mechanism exists.
33. Use the existing Draft -> Cancelled and Posted -> Reversed lifecycle conventions.
34. Protect all existing GR/MR/MI and other inventory document behavior with regression tests.
35. Preserve Company + Branch inventory conservation.
36. Validate location ownership against its warehouse.
37. Validate source availability after UOM conversion.
38. Do not treat serial-controlled items as lots.

## 38. Recommended Final Architecture

``` text
                    Stock Transfer UI
                           |
                           v
             StockTransfer Application Service
                           |
                           v
             Allocation Calculation
                           |
                           v
                Transfer Validation
                           |
                           v
          EXISTING Inventory Posting Engine
                           |
              +------------+------------+
              |                         |
              v                         v
        StockMovementAllocation     UOM conversion
              |
              v
         StockLedger
          /      \
         v        v
 StockBalance   ItemCost
                    |
                    v
              LotBalance (P3)
```

The key principle is:

**Stock Transfer is a document type inside the existing inventory
engine, not a separate inventory subsystem.**

## 39. Final Recommendation

### Implementation Gate — MUST PASS BEFORE CODING

Before the AI coding agent creates or changes database entities, it MUST inspect
the actual repository implementation of:

- `InventoryDocument`
- `InventoryDocumentLine`
- `StockMovementAllocation`
- `StockLedger`
- `StockBalance`
- `ItemCost`
- `LotBalance`
- UOM conversion entities/services
- Warehouse / WarehouseLocation
- existing inventory posting service
- existing document numbering
- existing audit/posting identity
- existing reversal implementation

The agent must answer:

1. What exact fields currently exist in `InventoryDocumentLine`?
2. What exact fields currently exist in `StockMovementAllocation`?
3. What is the actual business meaning of `StockMovementAllocation`?
4. Can one source line map to multiple destinations?
5. What exact database relationship links allocation and ledger?
6. Is allocation currently quantity allocation, cost allocation, or both?
7. Can that relationship represent:
   `1 source line -> many destination movements -> 1 OUT + many IN`
   without violating v3.2?
6. Which table owns the source inventory key?
7. Which table owns the destination inventory key?
8. Where is the UOM conversion snapshot stored?
9. How is source cost carried to destination?
10. What is the exact StockBalance unique key?
11. What SQL transaction isolation level is currently used?
12. What lock hints, if any, are currently used?
13. What exact index is used by the StockBalance locking query?
14. What is the exact deterministic lock order?
15. How is posting idempotency currently enforced?
13. How is document numbering currently generated?
14. How are posting dates and inventory periods validated?
15. How is reversal currently represented?

### Gate A — Repository Mapping

Produce a short report:

```text
A. Existing entity/schema mapping
B. Existing service/posting flow
C. Existing allocation/ledger relationship
D. Existing UOM conversion mechanism
E. Existing cost mechanism
F. Existing locking mechanism
G. Existing numbering/audit mechanism
H. Existing reversal mechanism
```

### Gate B — ST Design Proof

Using the actual repository schema, prove this exact scenario:

```text
Source:
WH-A/A01
1 BOX
1 BOX = 24 PCS

Destination:
WH-A/A02 = 10 PCS
WH-B/B01 = 8 PCS
WH-B/B02 = 6 PCS

Required ledger result:
OUT = -24 PCS
IN  = +10 PCS
IN  = +8 PCS
IN  = +6 PCS
```

The proof must show:

```text
DocumentLine
      |
      +-- Allocation 1
      +-- Allocation 2
      +-- Allocation 3

Allocation/Ledger relationships
      |
      +-- exactly how each OUT/IN row is linked
```

The agent must also prove:

```text
Total OUT BaseQty = Total IN BaseQty
Total OUT Value   = Total IN Value
```

If the existing schema cannot represent this correctly, STOP.

Do NOT silently:

- change allocation cardinality
- add duplicate bridge tables
- add a parallel StockTransfer ledger
- create a second StockBalance
- bypass the existing posting engine

If a locked v3.2 contract must change, obtain human approval BEFORE making
database/entity changes.

Only after Gate A and Gate B are reviewed and accepted may implementation
begin.


Implement Stock Transfer in two controlled stages:

### Stage 1

Non-lot:

``` text
Warehouse
Location
UOM conversion
1 source -> many destinations
Moving Average
Atomic posting
Concurrency protection
```

### Stage 2

Lot-aware:

``` text
LotId preservation
LotBalance
Lot reconciliation
```

Keep:

``` text
1 BOX -> LOT-A + LOT-B + LOT-C
```

as a separate Repack/Lot Split business operation unless the inventory
contract is formally revised.

This avoids breaking the locked v3.2 inventory architecture while still
supporting the warehouse/location transfer requirements.

**Coding gate:** do not start implementation until the actual repository
inspection has resolved the exact `StockMovementAllocation` ↔ `StockLedger`
relationship and demonstrated how one source line maps to multiple
destinations without violating the existing contract.

## Stock Transfer Posting Proof

Before implementation, the AI agent must prove the exact business scenario
using the actual repository model.

Scenario:

```text
1 BOX = 24 PCS

Source:
WH-A/A01
LOT001
1 BOX

Destinations:
WH-A/A02
10 PCS

WH-B/B01
8 PCS

WH-B/B02
6 PCS
```

The agent must produce a proof table equivalent to:

| Type | Warehouse | Location | Lot | UOM | Qty | Base Qty | Value |
|---|---|---|---|---|---:|---:|---:|
| OUT | WH-A | A01 | LOT001 | BOX | 1 | -24 | -RM240 |
| IN | WH-A | A02 | LOT001 | PCS | 10 | +10 | +RM100 |
| IN | WH-B | B01 | LOT001 | PCS | 8 | +8 | +RM80 |
| IN | WH-B | B02 | LOT001 | PCS | 6 | +6 | +RM60 |

Then prove:

```text
SUM(BaseQty) = 0
SUM(Value)   = 0
```

and prove the exact mapping:

```text
DocumentLine
    |
    +-- Allocation #1 -> OUT/IN relationship
    +-- Allocation #2 -> OUT/IN relationship
    +-- Allocation #3 -> OUT/IN relationship
```

The proof must use the actual repository's entities and relationships, not
invented pseudo-fields.

If the existing schema cannot represent the proof without violating the
v3.2 contract, STOP.

## Existing Inventory Regression Protection

Stock Transfer is an addition to an existing inventory engine. Shared posting
code must not be changed in a way that alters existing inventory behavior.

Before modifying shared inventory services, capture or run regression coverage
for every existing inventory document type.

Minimum regression scope:

```text
GR
MR
MI
Existing stock adjustment documents
Existing reversal documents
Existing costing tests
Existing lot/batch tests
```

After ST implementation:

1. Run all existing inventory tests.
2. Verify existing document types produce the same StockLedger behavior.
3. Verify existing document types produce the same StockBalance behavior.
4. Verify existing document types produce the same ItemCost behavior.
5. Verify existing document types produce the same LotBalance behavior.
6. Verify ST does not bypass shared posting controls.
7. Verify ST does not introduce a second stock mutation path.
8. Verify shared posting changes are documented with before/after evidence.

The AI agent MUST NOT modify existing GR/MR/MI or other document posting
behavior merely to make Stock Transfer easier to implement.

If shared engine changes are required, the agent must:

```text
1. Explain why the shared change is necessary.
2. Identify every affected document type.
3. Add/update regression tests.
4. Show before/after behavior.
5. Obtain human approval before merging the shared behavior change.
```

## Stock Mutation Boundary

Stock Transfer UI/application code must NOT directly manipulate inventory
state.

Prohibited pattern:

```csharp
_db.StockBalances.Update(...);
```

or equivalent direct updates from the UI/application orchestration layer.

Required conceptual flow:

```text
Blazor UI
    |
    v
StockTransfer Application Service
    |
    v
Allocation Calculation
    |
    v
Transfer Validation
    |
    v
EXISTING Inventory Posting Engine
    |
    +--> StockLedger
    +--> StockBalance
    +--> ItemCost
    +--> LotBalance
```

The existing inventory posting engine remains the authoritative stock mutation
boundary.

## Final Quality Gate — Ready for AI Implementation

The plan is implementation-ready only when all of the following are true:

- [ ] Actual repository schema has been inspected.
- [ ] Existing GR/MR/MI and other inventory regression behavior is protected.
- [ ] Logical field responsibilities have not been mistaken for migration requirements.
- [ ] Actual business meaning of StockMovementAllocation has been verified.
- [ ] Actual business meaning of StockMovementAllocation has been verified.
- [ ] Existing transaction isolation/lock hints/index usage has been verified.
- [ ] `InventoryDocumentLine` source responsibility is confirmed.
- [ ] `StockMovementAllocation` destination responsibility is confirmed.
- [ ] Existing Ledger/Allocation cardinality is proven compatible.
- [ ] One source -> many destinations is proven with the 1 BOX / 24 PCS example.
- [ ] No duplicate inventory architecture is required.
- [ ] UOM conversion snapshot is identified.
- [ ] Source/destination base quantity conservation is defined.
- [ ] Source/destination value conservation is defined.
- [ ] Same-warehouse cost behavior is defined.
- [ ] Cross-warehouse MAV behavior uses the existing algorithm.
- [ ] StockBalance lock keys and lock order are confirmed.
- [ ] Posting idempotency is confirmed.
- [ ] Document numbering is confirmed.
- [ ] Audit/posting identity is confirmed.
- [ ] Reversal allocation mapping is confirmed.
- [ ] Phase 2 lot restrictions are confirmed.
- [ ] Phase 3 LotBalance behavior is confirmed.
- [ ] Lot transformation/repack scope is explicitly decided.
- [ ] Exact end-to-end acceptance test passes conceptually.
- [ ] UI AvailableQty is explicitly non-authoritative.
- [ ] Whole/decimal UOM behavior is confirmed from existing Item/UOM rules.
- [ ] Physical quantity conservation is proven.
- [ ] Inventory value conservation is proven.
- [ ] Quantity/currency/unit-cost precision rules are confirmed.
- [ ] Deterministic allocation/ledger ordering is confirmed.
- [ ] Existing transaction isolation is preserved unless explicitly approved otherwise.
- [ ] Stock Transfer Posting Proof has been completed against the actual repository.

**If any checkbox above cannot be proven from the repository or the locked
v3.2 inventory contract, the AI agent must stop and ask for clarification
rather than guessing.**
