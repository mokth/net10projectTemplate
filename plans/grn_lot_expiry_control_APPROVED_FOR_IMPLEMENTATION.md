# GRN Lot / Expiry Control Enhancement

Repository: `mokth/net10projectTemplate`  
Branch verified: `production`  
Verified HEAD: `8055058f1911e83de7da6db28327dc604f718e22`

# Objective

Separate **lot tracking** from **expiry-date enforcement** for purchase Goods Receipt (GRN).

Introduce an item-level expiry policy:

- `NONE` — lot number is still tracked, but expiry is not captured/required.
- `OPTIONAL` — expiry may be entered but may be blank.
- `REQUIRED` — expiry must be entered.

For lot-controlled GRN lines, Lot No remains required/auto-generated exactly as today. Expiry validation must follow the new item policy instead of `LotControl` alone.

When expiry is entered, validate it against the **GRN transaction date**, not the application/server current date.

# Confirmed Current Problems

## 1. Item master has no separate expiry policy

Verified files:

- `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`
- `ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs`

Current model has:

- `StockControl`
- `LotControl`

There is no field that distinguishes:

- lot-controlled item with no expiry;
- lot-controlled item with optional expiry;
- lot-controlled item with mandatory expiry.

Required correction: add an explicit expiry-control policy to `IvStockMaster`.

## 2. GRN UI hard-requires expiry whenever LotControl is true

Verified file:

`ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`

Method:

`ValidateStockReceiveLine(...)`

Current rule:

`LotControl == true` + `ExpiryDate == null` -> validation error.

Required correction: validation must use item `ExpiryControl`.

## 3. GRN core service hard-requires expiry whenever LotControl is true

Verified file:

`ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`

Current stock-line validation:

- loads `IvStockMaster`;
- reads `item.LotControl`;
- allocates/accepts Lot No;
- rejects null expiry for every lot-controlled item.

Required correction: re-read and enforce the authoritative item expiry policy.

## 4. Posting would still fail even if only GRN save validation were changed

Verified file:

`ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

Method:

`BuildMrPostLineAsync(...)`

GR posting ultimately reuses the stock-in posting path and currently rejects null expiry for every lot-controlled item.

Required correction: posting must enforce the same `NONE / OPTIONAL / REQUIRED` policy.

## 5. Database already supports null expiry

Verified files:

- `ErpWeb.Model/Entities/Inventory/IvLot.cs`
- `ErpWeb.Model/Configurations/Inventory/IvLotConfiguration.cs`

`IvLot.ExpiryDate` is already nullable.

GR transaction detail expiry is also already nullable in the current workflow.

Therefore **no change is required to IvLot or transaction-detail expiry nullability**.

# Scope

Included:

- item-master expiry policy;
- idempotent SQL upgrade;
- item-master create/edit/view;
- GRN PO-line lookup propagation;
- GRN line UI behavior;
- GRN save/update validation;
- GRN post validation;
- transaction-date-based expiry validation;
- focused regression tests.

# Non-Goals

DO NOT change:

- lot-number generation algorithm;
- PO outstanding/received quantity calculations;
- GRN unit-price or purchase-cost authority;
- inventory costing;
- FIFO / Moving Average / Standard Cost logic;
- `IvBalLoc` stock-slice identity;
- stock ledger architecture;
- posting transaction boundaries;
- rollback quantity/cost logic;
- period-close rules;
- production FG expiry logic;
- FEFO picking/allocation;
- historical posted lot expiry values;
- unrelated receipt UI;
- bulk expiry-entry UX in this task.

A separate UX enhancement can later add “Apply expiry to selected/all lines” for companies where expiry is required.

# Files to Change

## Model / EF

1. `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`
2. `ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs`
3. **New:** `ErpWeb.Model/Entities/Inventory/IvExpiryControlModes.cs`

## Item Master Core

4. `ErpWeb.Core/Inventory/IvMasterResults.cs`
5. `ErpWeb.Core/Inventory/IvStockMasterService.cs`

## Item Master UI

6. `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
7. `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.cs`

## GRN Core / Contracts

8. `ErpWeb.Core/Inventory/IIvGoodsReceiptService.cs`
9. `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`
10. `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

## GRN UI

11. `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`
12. `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`

## SQL

13. **New:** `scripts/alter-ivstockmaster-expirycontrol.sql`

## Tests

14. `ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs`
15. `ErpWeb.Tests/Inventory/Transaction/IvGoodsReceiptServiceTests.cs`
16. `ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs`

# Database Changes

## Table

`dbo.IvStockMaster`

## New column

`ExpiryControl nvarchar(10) NOT NULL`

Allowed application values:

- `NONE`
- `OPTIONAL`
- `REQUIRED`

Default for newly created database rows:

`NONE`

## Upgrade / backfill rule

The SQL script MUST be idempotent.

On the first deployment only:

1. add `ExpiryControl` with default `NONE`;
2. set existing `LotControl = 1` items to `REQUIRED`;
3. leave existing non-lot-controlled items as `NONE`.

This preserves current production behavior for existing lot-controlled items.

IMPORTANT:

The backfill MUST execute only when the column is first created. Re-running the script MUST NOT overwrite policies subsequently changed by users.

Recommended allowed-values check constraint:

`ExpiryControl IN ('NONE','OPTIONAL','REQUIRED')`

## No other schema changes

MUST NOT change:

- `IvLot.ExpiryDate`;
- transaction detail `ExpiryDate`;
- stock balances;
- costing tables;
- ledger tables.

# Exact Code Changes

## 1. Add expiry-control constants

Create:

`ErpWeb.Model/Entities/Inventory/IvExpiryControlModes.cs`

Provide canonical values:

- `None = "NONE"`
- `Optional = "OPTIONAL"`
- `Required = "REQUIRED"`

Provide a validation/allowed-values collection or equivalent reusable helper.

Do not duplicate free-form string literals throughout the codebase.

## 2. Extend IvStockMaster

File:

`ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`

Add:

`public string ExpiryControl { get; set; } = IvExpiryControlModes.None;`

File:

`ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs`

Configure:

- max length 10;
- required;
- DB default `NONE`;
- `ValueGeneratedNever()` consistent with the existing master flags/policies.

## 3. Extend Item Master edit model

File:

`ErpWeb.Core/Inventory/IvMasterResults.cs`

Add `ExpiryControl` to `IvStockMasterEditVm`.

Default must be `NONE`.

No requirement to add this field to the item-list grid/export in this task.

## 4. Item Master service rules

File:

`ErpWeb.Core/Inventory/IvStockMasterService.cs`

### Save validation

Normalize expiry policy to canonical uppercase values.

MUST reject an unsupported value.

Rules:

```text
LotControl = false
    ExpiryControl MUST be NONE

LotControl = true
    ExpiryControl may be NONE, OPTIONAL, or REQUIRED
```

Keep the existing rule:

```text
LotControl = true requires StockControl = true
```

### Mapping

Update:

- `ApplyEditableFields(...)`
- `MapEditVm(...)`

to persist/read `ExpiryControl`.

### Structural-lock rule

DO NOT add `ExpiryControl` to the existing
`EnsureStructuralFieldsUnlockedAsync(...)` stock-structure lock.

Reason: changing expiry policy does not change stock-slice identity, quantity, valuation, UOM, or LotControl.

Historical `IvLot.ExpiryDate` values MUST remain unchanged.

## 5. Item Master UI

Files:

- `IvStockMasterEntry.razor`
- `IvStockMasterEntry.razor.cs`

### View mode

Inventory card must show:

`Expiry control: None / Optional / Required`

### Edit/New mode

Add an `Expiry control` selector beside `Lot control`.

Options:

- None
- Optional
- Required

Behavior:

```text
LotControl = false
    selector disabled
    value forced to NONE

LotControl = true
    selector enabled
```

When the user turns Lot Control off, set `ExpiryControl = NONE`.

`CreateBlank()` default = `NONE`.

`Clone(...)` MUST copy `ExpiryControl`.

## 6. Extend GRN read/lookup contracts

File:

`ErpWeb.Core/Inventory/IIvGoodsReceiptService.cs`

Add `ExpiryControl` to:

- `IvGoodsReceiptLineDto`
- `IvGoodsReceiptPoLineLookupRow`

DO NOT add `ExpiryControl` to `IvGoodsReceiptLineRequest`.

The client/UI MUST NOT be the authority for expiry policy.

The core service must always re-read `IvStockMaster`.

## 7. GRN PO lookup

File:

`ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`

Method:

`SearchPoLinesAsync(...)`

When loading item master data, load:

- `LotControl`
- `ExpiryControl`
- existing default warehouse/location fields

Populate `IvGoodsReceiptPoLineLookupRow.ExpiryControl`.

For indirect/non-stock GR, use `NONE`.

## 8. GRN Get/Edit document

File:

`IvGoodsReceiptService.cs`

Method:

`GetAsync(...)`

Current code builds a dictionary containing only `LotControl`.

Replace it with item policy metadata containing:

- `LotControl`
- `ExpiryControl`

Populate both into every `IvGoodsReceiptLineDto`.

This ensures edit mode uses the current item policy.

## 9. GRN line VMs

File:

`ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`

Add `ExpiryControl` to:

- `IvGoodsReceiptLineVm`
- `IvGoodsReceiptLinePopupVm`
- `IvGoodsReceiptPoPickerRow`

Update:

- `ApplyDocument(...)`
- `IvGoodsReceiptPoPickerRow.FromLookup(...)`
- `AddFromPoAsync()`
- `EditLineAsync(...)`
- `ApplyPopupToLine(...)`

Imported PO lines must receive the item's expiry policy automatically.

## 10. GRN expiry UI behavior

File:

`ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`

Existing Expiry Date editor must behave as:

### NONE

- disabled;
- blank;
- not marked required.

### OPTIONAL

- enabled;
- Clear button enabled;
- blank allowed;
- not marked required.

### REQUIRED

- enabled;
- visually marked required;
- blank blocked.

Lot No behavior remains unchanged.

Use the GRN header transaction date as the UI minimum date:

`Header.TrxDate.Date`

Do not use `AppToday` as the expiry minimum.

The core service remains authoritative even if client validation is bypassed.

## 11. Resolve GRN transaction date before line validation

File:

`IvGoodsReceiptService.cs`

Methods:

- `SaveNewAsync(...)`
- `UpdateAsync(...)`

Current flow validates lines before resolving the authoritative movement date.

Change order:

1. normalize transaction type;
2. resolve movement date with existing `IvStockMovementRules.ResolveMovementDate(...)`;
3. fail if movement date is invalid;
4. run existing period-open check at the existing safe boundary;
5. pass resolved `trxDate` into line validation;
6. continue existing save/update flow.

Do not change the running-number, cost-evidence, or transaction semantics except as required to make `trxDate` available to validation.

## 12. GRN core expiry validation

File:

`IvGoodsReceiptService.cs`

Update `ValidateLinesAsync(...)` / underlying line-validation method to accept the resolved GRN transaction date.

Authoritative rules:

```text
if item.LotControl == false:
    ToLotNo = empty
    ExpiryDate = null

if item.LotControl == true:
    Lot No remains required/auto-allocated

    switch item.ExpiryControl:

        NONE:
            ExpiryDate = null
            never require expiry

        OPTIONAL:
            ExpiryDate may be null
            if supplied:
                ExpiryDate.Date >= GRN.TrxDate.Date

        REQUIRED:
            ExpiryDate must not be null
            ExpiryDate.Date >= GRN.TrxDate.Date
```

Do not validate expiry against `_dates.Today`.

This allows a valid backdated GRN where expiry is after the receipt date but before today's date.

## 13. Posting guard MUST match save guard

File:

`ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

Method:

`BuildMrPostLineAsync(...)`

This method is used by the stock-in posting path that GR ultimately reaches.

Pass the locked batch transaction date into the line builder.

Re-read authoritative `IvStockMaster.ExpiryControl`.

Enforce:

```text
NONE:
    expiry passed to new-lot creation = null

OPTIONAL:
    blank accepted
    supplied expiry must be >= batch transaction date

REQUIRED:
    blank rejected
    supplied expiry must be >= batch transaction date
```

Lot No remains mandatory for all lot-controlled items.

Posting MUST NOT trust a UI-supplied expiry policy.

### Shared posting-path note

`BuildMrPostLineAsync(...)` is shared by stock-in posting paths.

This task MUST NOT relax or redesign Misc Receipt / Customer Return entry UI.

The posting rule must nevertheless be safe for all callers:

- `REQUIRED` remains fail-closed;
- `OPTIONAL`/`NONE` may accept null expiry;
- no quantity/cost/ledger behavior changes.

## 14. IvLot creation

No structural change is required.

Current repository method:

`IIvStockPostingRepository.FindOrCreateLotAsync(...)`

already accepts nullable `DateTime? expiryDate`.

For a newly created lot:

- NONE/OPTIONAL blank -> `IvLot.ExpiryDate = null`;
- OPTIONAL/REQUIRED supplied -> persist supplied date.

Do not invent a placeholder date such as `31/12/2099`.

# Transaction / Execution Order

## GRN Save / Update

1. validate permission/context;
2. normalize GR transaction type;
3. resolve authoritative GR transaction date;
4. validate period using existing guard;
5. load authoritative PO/item data;
6. enforce LotControl + ExpiryControl;
7. preserve existing PO quantity/draft checks;
8. preserve existing receipt-cost evidence calculation;
9. save NEW batch/detail rows;
10. commit existing DB transaction.

## GRN Post

Keep the existing posting architecture:

1. existing branch stock transaction lock;
2. lock/re-read batch;
3. lock/re-read PO;
4. validate PO receipt quantity;
5. begin existing stock-ledger context;
6. re-read authoritative item master;
7. enforce expiry policy;
8. find/create lot and balance;
9. preserve existing quantity/cost/history writes;
10. update PO received quantities;
11. complete existing ledger;
12. commit.

No new posting engine is permitted.

# Authority Rules

- `IvStockMaster.ExpiryControl` is the authority for whether expiry is required.
- `IvGoodsReceiptLineRequest` is NOT an authority for expiry policy.
- GR UI policy values are display/UX state only.
- Core save/update MUST re-read item master.
- Posting MUST re-read item master again.
- `IvLot.ExpiryDate` is stored lot metadata, not costing authority.
- Expiry must never influence unit cost, stock value, FIFO cost layer, or PO pricing.

# Invariants

1. `LotControl = false` -> no lot and no expiry on a new GR stock line.
2. `LotControl = true` -> lot identity remains mandatory.
3. `ExpiryControl = NONE` -> expiry is null for the receipt.
4. `ExpiryControl = OPTIONAL` -> null is valid.
5. `ExpiryControl = REQUIRED` -> null is invalid.
6. Any supplied expiry must be on/after the receipt transaction date.
7. Blank expiry MUST NOT be converted to a fake/sentinel date.
8. Existing posted historical expiry values MUST NOT be rewritten.
9. Expiry policy MUST NOT alter stock quantity or valuation authority.

# Rollback / Reversal

No rollback architecture change.

Existing GR rollback must continue to reverse:

- stock quantity;
- inventory history/ledger effects;
- PO received quantity;
- costing effects according to existing logic.

A null `IvLot.ExpiryDate` MUST NOT block rollback.

Do not delete or rewrite historical lot metadata as part of rollback.

# Concurrency / Locking

No new locking architecture.

Preserve:

- existing GR save transaction;
- `BranchStockTransactionLock`;
- existing PO locking;
- stock master locking/re-read during post;
- stock balance locking;
- stock ledger boundaries.

Expiry policy is validation metadata only.

# Tests

## `IvStockMasterServiceTests.cs`

Add/adjust tests:

1. `SaveNew_LotControl_NoneExpiryControl_Succeeds`
2. `SaveNew_LotControl_OptionalExpiryControl_Succeeds`
3. `SaveNew_LotControl_RequiredExpiryControl_Succeeds`
4. `SaveNew_NonLot_WithOptionalOrRequiredExpiryControl_Fails`
5. `SaveNew_InvalidExpiryControl_Fails`
6. `Get_ReturnsExpiryControl`
7. copy/new model preserves `ExpiryControl`

Keep existing:

`LotControl_WithoutStockControl_Fails`

## `IvGoodsReceiptServiceTests.cs`

Replace the current broad test:

`SaveNew_requires_expiry_for_lot_controlled_item`

with policy-specific coverage.

Required tests:

1. `SaveNew_Lot_None_AllowsBlankExpiry`
   - lot is allocated/preserved;
   - save succeeds;
   - detail expiry is null.

2. `SaveNew_Lot_Optional_AllowsBlankExpiry`

3. `SaveNew_Lot_Optional_PersistsEnteredExpiry`

4. `SaveNew_Lot_Required_RejectsBlankExpiry`

5. `SaveNew_Lot_Required_AcceptsValidExpiry`

6. `SaveNew_EnteredExpiryBeforeTransactionDate_Fails`

7. `SaveNew_BackdatedReceipt_ExpiryAfterReceiptButBeforeToday_Succeeds`
   - proves validation uses GRN transaction date, not server today.

8. existing automatic-lot tests remain passing:
   - blank lot auto-allocation;
   - manual lot preservation;
   - same-item generated lots remain unique.

## `IvInventoryPostingServiceTests.cs`

Add:

1. `PostGoodsReceipt_LotNone_BlankExpiry_Succeeds`
   - lot exists;
   - `IvLot.ExpiryDate == null`;
   - stock quantity posted correctly.

2. `PostGoodsReceipt_LotOptional_BlankExpiry_Succeeds`

3. `PostGoodsReceipt_LotRequired_BlankExpiry_FailsClosed`
   - construct/save a draft in a way that proves posting independently enforces current policy.

4. `PostGoodsReceipt_EnteredExpiryBeforeBatchDate_FailsClosed`

5. `RollbackGoodsReceipt_WithNullLotExpiry_Succeeds`
   - stock quantity reverses;
   - PO received quantity reverses;
   - existing ledger/rollback guarantees remain intact.

# Implementation Order

STEP 1 — Add failing focused tests for item expiry policy and GR blank-expiry behavior.

STEP 2 — Add `IvExpiryControlModes` and `IvStockMaster.ExpiryControl`.

STEP 3 — Add idempotent SQL upgrade/backfill script.

STEP 4 — Update EF configuration and Item Master VM/service mapping/validation.

STEP 5 — Update Item Master UI.

STEP 6 — Propagate expiry policy through GR PO lookup and GR document DTOs.

STEP 7 — Update GR save/update validation and transaction-date rule.

STEP 8 — Update GR UI enable/required/clear behavior.

STEP 9 — Update shared stock-in posting guard so GR post cannot contradict GR save.

STEP 10 — Run focused inventory master + GR service + posting tests.

STEP 11 — Run complete solution build and inventory/procurement regression tests.

# Regression Areas

Verify unchanged behavior for:

- PO import to GRN;
- partial PO receipt;
- multiple GRNs against one PO;
- PO received/balance quantity;
- automatic Lot No allocation;
- manual Lot No entry;
- GR save/update/delete;
- GR post;
- GR rollback;
- inventory balances;
- lot inquiry;
- inventory history;
- costing;
- stock ledger;
- period close;
- non-stock goods receipt;
- existing lot-controlled items configured as REQUIRED after migration.

# Do-Not Rules

DO NOT:

- make ExpiryDate globally mandatory because LotControl is true;
- auto-create fake expiry dates;
- trust an expiry-policy value coming from the browser;
- modify costing logic;
- modify PO cost/price authority;
- modify lot-number generation;
- change stock-slice keys;
- add expiry to `IvBalLoc` identity;
- weaken posting/rollback locks;
- rewrite historical `IvLot` expiry data;
- backfill all existing items to NONE;
- reset user-selected expiry policies when the SQL script is rerun;
- perform unrelated refactoring.

# Acceptance Criteria

- [ ] Existing lot-controlled items are backfilled to `REQUIRED`, preserving pre-change behavior.
- [ ] Existing non-lot items are backfilled to `NONE`.
- [ ] SQL upgrade is safe to run more than once without resetting user configuration.
- [ ] Item Master supports `NONE`, `OPTIONAL`, and `REQUIRED`.
- [ ] Non-lot-controlled items cannot be saved with an active expiry policy.
- [ ] GRN PO import automatically carries the item's expiry policy.
- [ ] Lot-controlled + `NONE` can be saved with blank expiry.
- [ ] Lot-controlled + `OPTIONAL` can be saved with blank expiry.
- [ ] Lot-controlled + `OPTIONAL` can store a supplied expiry.
- [ ] Lot-controlled + `REQUIRED` cannot be saved or posted with blank expiry.
- [ ] Supplied expiry is validated against GRN transaction date.
- [ ] Blank expiry creates a valid `IvLot` with nullable expiry where a new lot is created.
- [ ] Existing lot-number generation behavior is unchanged.
- [ ] Existing PO receipt quantity behavior is unchanged.
- [ ] Existing costing and stock-ledger behavior is unchanged.
- [ ] GRN rollback works when the lot expiry is null.
- [ ] Focused tests pass.
- [ ] Full solution build passes.
- [ ] Inventory/Procurement regression tests pass.

# Approval Status

APPROVED FOR IMPLEMENTATION
