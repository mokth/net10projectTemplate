# Blank-to-Auto UI Number / Lot Standardization — REVIEWED APPROVED 10/10

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified commit:** `982e405139ae2626d287fb5a30f38c40cc1e5577`  
**Status:** **APPROVED FOR IMPLEMENTATION**  
**Target agents:** Cursor / Grok / Claude Code / Codex

# Objective

Standardize safe user-editable identifier inputs so that:

```text
blank -> system resolves/generates the value
typed -> preserve the typed value
```

Auto-capable fields MUST NOT fail only because the user leaves them blank.

The implementation MUST NOT alter Posting, Rollback, Costing, Stock Ledger, valuation, month-end, quantity authority, system document identities, or existing source-document relationships.

# Confirmed Problems

## Inventory Ref No

The following services already implement:

```text
blank -> BatchNo
AUTO -> BatchNo
manual -> manual
```

through existing `NormalizeRefNo(...)` methods:

- `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`
- `ErpWeb.Core/Inventory/IvMiscIssueService.cs`
- `ErpWeb.Core/Inventory/IvMiscReceiptService.cs`
- `ErpWeb.Core/Inventory/IvScrapService.cs`
- `ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`
- `ErpWeb.Core/Inventory/IvStockReturnService.cs`
- `ErpWeb.Core/Inventory/IvStockTransferService.cs`
- `ErpWeb.Core/Inventory/IvVendorReturnService.cs`

Their UI code-behind currently initializes `RefNo = "AUTO"` and their Razor textboxes use `NullText="AUTO"`.

Required: New-mode Ref No must display blank with `Auto-generated if blank`.

## Goods Receipt destination lot

`IvGoodsReceipt.razor.cs -> ValidateStockReceiveLine(...)` does not require a nonblank lot.

`IvGoodsReceiptService` already allocates a lot through its existing lot allocation path.

Required: remove misleading required styling only.

## Misc Receipt / Stock Return destination lot

Current UI `ValidatePopup()` rejects a blank lot-controlled `ToLotNo`.

Both pages already have existing `NextLotNo()` generation behavior.

Required: when the line is committed and the lot is blank, generate first, then run current validation.

Core Inventory services remain unchanged as the defensive boundary.

## Stock Transfer destination lot

Current UI rejects a blank destination lot.

The page already has a database-aware generator in `OnGenerateDestLotAsync()` using:

- `IvLotNumberGenerator.AllocateAsync(...)`;
- current document destination lots;
- `Lookups.LotExistsAsync(...)`.

Required: blank commit must use the same generator.

## Daily Production OutputLotNo

`ProductionOutputService.CreateAsync(...)` and `UpdateAsync(...)` currently persist blank `OutputLotNo`.

The value later participates in production-balance lot identity.

Required:

```text
manual lot -> manual trimmed lot
blank lot -> Daily Production DocumentNo
```

The existing replay comparator also compares OutputLotNo and MUST use the same resolution rule, otherwise idempotent retries break.

## Finished Good Reference

`ProductionFinishedGoodReceiptService.SaveCoreAsync(...)` allocates/loads `BatchNo` before assigning `RefNo`.

Required:

```text
manual RefNo -> manual trimmed RefNo
blank RefNo -> BatchNo.ToString(InvariantCulture)
```

## Finished Good destination lot

Current service already resolves:

```text
entered destination lot
-> source.PhysicalLotNo
-> source.LotNo
```

Required: UI wording only. Do not change service behavior.

# Scope / Non-Goals

## Included

- blank/typed Inventory Ref No UI standardization;
- Goods Receipt destination-lot wording/styling;
- Misc Receipt blank destination-lot generation;
- Stock Return blank destination-lot generation;
- Stock Transfer blank destination-lot generation;
- Daily Production blank OutputLotNo fallback;
- Daily Production replay normalization for the fallback;
- FG blank RefNo fallback;
- FG destination-lot wording;
- focused validation/tests required by these changes.

## MUST NOT change

- QT / SO / DO / Sales Invoice / Sales CN/DN document identity;
- PR / PO / Purchase Invoice / Purchase CN/DN document identity;
- Self-Billed document identity;
- Work Order number;
- Inventory `BatchNo`;
- Stock Count `CountNo`;
- Material Issue `BatchNo`;
- source lot / `FrLotNo`;
- source invoice / PO / SO / DO / PR links;
- supplier invoice/document numbers;
- customer PO/RFQ;
- external/legal references;
- TIN/SST/GST/registration numbers;
- machine serial numbers;
- bank account numbers;
- barcode;
- Sales CN return-stock logic.

System document keys remain generated/read-only exactly as today.

# Exact Files

## UI

1. `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`
2. `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`
3. `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor`
4. `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor.cs`
5. `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor`
6. `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.cs`
7. `ErpWeb.UI/Inventory/Transactions/IvScrap.razor`
8. `ErpWeb.UI/Inventory/Transactions/IvScrap.razor.cs`
9. `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor`
10. `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor.cs`
11. `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor`
12. `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor.cs`
13. `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
14. `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.cs`
15. `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor`
16. `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor.cs`
17. `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor`
18. `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

## Core — narrowly scoped

19. `ErpWeb.Core/Production/ProductionOutputService.cs`
20. `ErpWeb.Core/Production/ProductionOutputService.Materials.cs`
21. `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`

## Tests

22. `ErpWeb.Tests/Production/Transaction/ProductionOutputEntryServiceTests.cs`
23. `ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

Inventory service/posting/ledger tests are regression targets and MUST NOT be rewritten to accommodate the feature.

# Database Changes

**NONE.**

Do not add/alter:

- table;
- column;
- index;
- constraint;
- migration;
- SQL upgrade script;
- trigger;
- procedure;
- view.

Verified existing limits:

```text
ProductionOutput.DocumentNo  <= 30
ProductionOutput.OutputLotNo <= 50
FG RefNo                     <= 30
FG destination lot           <= 50
Inventory RefNo              <= existing 50-char UI/service contract
```

# Exact Changes

## A. Inventory Ref No — all eight pages

### Code-behind

Change only RefNo defaults:

```csharp
RefNo = "AUTO"
```

to:

```csharp
RefNo = string.Empty
```

and:

```csharp
public string RefNo { get; set; } = "AUTO";
```

to:

```csharp
public string RefNo { get; set; } = string.Empty;
```

MUST NOT change:

```csharp
BatchNoDisplay = "AUTO";
```

That is a system batch display and is correct.

Do not alter loaded Edit/View values.

### Razor

Change only the editable Ref No textbox:

```text
NullText="Auto-generated if blank"
```

Keep:

- editable in current editable states;
- `maxlength="50"`;
- existing read-only rules.

Do not add required-field CSS.

### Services

DO NOT change any Inventory `NormalizeRefNo(...)`.

Expected behavior remains:

```text
blank -> BatchNo
AUTO  -> BatchNo
typed -> typed
```

`AUTO` stays supported at service level for backward compatibility.

## B. Goods Receipt destination lot

File:

`ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`

For `LinePopup.ToLotNo`:

- remove required-field styling;
- set NullText to `Auto-generated if blank`;
- preserve `maxlength="50"`;
- preserve manual entry;
- preserve Generate button.

DO NOT modify `IvGoodsReceiptService` lot allocation.

## C. Misc Receipt destination lot

Files:

- `IvMiscReceipt.razor`
- `IvMiscReceipt.razor.cs`

### Razor

For `Popup.ToLotNo`:

- remove required-field styling;
- set NullText to `Auto-generated if blank`;
- preserve max length 50.

### `OnCommitLine()`

Keep the method synchronous.

Before calling `ValidatePopup()`:

```text
IF Popup.LotControl AND Popup.ToLotNo is blank
    try:
        Popup.ToLotNo = NextLotNo()
    catch exception:
        PopupError = exception.Message
        return
```

Then execute the current validation and line-apply logic unchanged.

### `ValidatePopup()`

Remove only the UI check that rejects blank `ToLotNo`.

Keep all existing:

- expiry required for lot-controlled item;
- expiry >= `AppToday`;
- item/UOM/class/status validation;
- warehouse/location validation;
- quantity validation;
- cost evidence / zero-cost rules;
- reason validation.

### Service

`IvMiscReceiptService` MUST remain unchanged.

Direct invalid service calls with a blank lot remain rejected.

## D. Stock Return destination lot

Files:

- `IvStockReturn.razor`
- `IvStockReturn.razor.cs`

Use the same pattern as Misc Receipt.

### Razor

- remove required-field styling from destination lot;
- NullText = `Auto-generated if blank`;
- keep max length 50.

### `OnCommitLine()`

Keep synchronous.

Before `ValidatePopup()`:

```text
IF Popup.LotControl AND Popup.ToLotNo is blank
    try:
        Popup.ToLotNo = NextLotNo()
    catch exception:
        PopupError = exception.Message
        return
```

Then run current validation.

### MUST preserve

- source posted invoice requirement;
- source invoice-line requirement;
- expiry validation;
- quantity rules;
- warehouse/location rules;
- manual lot value.

### Service

`IvStockReturnService` MUST remain unchanged.

## E. Stock Transfer destination lot

Files:

- `IvStockTransfer.razor`
- `IvStockTransfer.razor.cs`

### Razor

Change destination-lot textbox from visually required to optional-auto:

```text
CssClass="flex-grow-1"
NullText="Auto-generated if blank"
maxlength="50"
```

Remove only `required-field`.

Source lot remains required/read-only from selected balance.

### Generator refactor

Refactor the current destination generator into one private async helper so both paths use exactly the same allocation logic:

- Generate button;
- blank line commit.

The helper MUST continue using:

```text
IvLotNumberGenerator.AllocateAsync(...)
current document destination lots
Lookups.LotExistsAsync(Popup.ICode, lot)
```

Do not duplicate the algorithm.

### `OnGenerateDestLotAsync()`

Call the shared helper and preserve current error display behavior.

### `OnCommitLine()`

Convert only this page's commit handler to `async Task`.

Before validation:

```text
IF Popup.LotControl AND Popup.ToLotNo is blank
    success = await shared destination-lot allocator
    IF not success
        keep popup open
        preserve PopupError
        return
```

Then call existing `ValidatePopup()`.

### MUST preserve

- selected `FromBalLocId`;
- source lot requirement;
- source lot == selected balance lot;
- destination lot != source lot;
- existing destination-lot collision validation;
- `OriginalToLotNo`;
- split-to-new-lot behavior;
- quantity/UOM/status/cost behavior.

### Service

`IvStockTransferService` MUST remain unchanged.

## F. Daily Production OutputLotNo

Files:

- `PrDailyProductionEntry.razor`
- `ProductionOutputService.cs`
- `ProductionOutputService.Materials.cs`

### Razor

For `OutputLotNo`:

```text
NullText="Auto-generated if blank"
maxlength="50"
```

Do not mark required.

### Shared resolver

Add one private static resolver in the partial `ProductionOutputService`:

```text
ResolveOutputLotNo(requestedLot, documentNo)

lot = trim requestedLot
IF lot is blank
    return documentNo
ELSE
    return lot
```

This resolver MUST be used by Create, Update, and replay comparison.

### Manual-length validation

Before persistence:

```text
trimmed manual request lot length > 50 -> validation failure
```

Do not let EF/database truncation/errors enforce this.

The generated fallback is safe because `DocumentNo` max length is 30.

### `CreateAsync(...)`

Keep existing request permission, tenant scope, sequence, Work Order, quantity, operation, and reference validation.

Keep existing `AllocateDocumentNoAsync(...)`.

After `documentNo` is allocated:

```text
outputLotNo = ResolveOutputLotNo(request.OutputLotNo, documentNo)
```

Persist `outputLotNo`.

Do not allocate a separate lot sequence.

### `UpdateAsync(...)`

For an existing NEW output:

```text
manual nonblank -> trimmed manual value
blank -> output.DocumentNo
```

No number regeneration.

### `ReplayPayloadMatches(...)`

File:

`ProductionOutputService.Materials.cs`

Replace raw request lot comparison with resolved comparison:

```text
incomingResolvedLot =
    ResolveOutputLotNo(request.OutputLotNo, existing.DocumentNo)

existing.OutputLotNo == incomingResolvedLot
```

This is mandatory for idempotency.

Semantics:

```text
first request blank -> persisted DocumentNo lot
same PostingRequestId + blank retry -> MATCH
first request manual X + blank retry -> MISMATCH
first request blank + retry manual DocumentNo -> MATCH
first request manual X + retry manual X -> MATCH
```

Do not alter material comparison logic in this method.

### MUST NOT modify

- `ProductionOutputService.Posting.cs`;
- `ProductionOutputService.Rollback.cs`;
- production material fact calculation;
- WIP valuation;
- production balance movement logic;
- operation eligibility;
- posting coordinator behavior;
- posting link semantics.

## G. Finished Good RefNo

Files:

- `PrFinishedGoodReceiptEntry.razor`
- `ProductionFinishedGoodReceiptService.cs`

### Razor

Reference textbox:

```text
NullText="Auto-generated from batch if blank"
```

Keep existing editability/length behavior.

### SaveCoreAsync

Normalize before length validation:

```text
requestedRefNo = trim(request.RefNo)
IF requestedRefNo length > 30
    validation error
```

After new/existing `BatchNo` is available:

```text
resolvedRefNo =
    requestedRefNo when nonblank
    otherwise BatchNo.ToString(CultureInfo.InvariantCulture)
```

Assign:

```text
r.Batch.RefNo = resolvedRefNo
```

Do not change `BatchNo`.

Do not change FG posting source identity.

## H. Finished Good destination lot UI

File:

`PrFinishedGoodReceiptEntry.razor`

Change:

```text
Required lot
```

to:

```text
Uses source lot if blank
```

No service change.

Existing fallback MUST stay:

```text
input.LotNo
-> source.PhysicalLotNo
-> source.LotNo
```

# Transaction / Execution Order

## Inventory Ref No

1. User leaves blank or types Ref No.
2. UI sends value.
3. Existing service allocates BatchNo.
4. Existing `NormalizeRefNo(...)` resolves RefNo.
5. Draft saves.
6. Existing POST/ROLLBACK operates on persisted transaction.

## Inventory destination lot

1. User edits lot-controlled line.
2. Manual lot -> preserve.
3. Blank lot -> existing page generator supplies value.
4. Current UI validation runs.
5. Current service validation runs unchanged.
6. Draft saves.
7. Existing POST/costing/rollback runs unchanged.

## Daily Production

1. Existing permission/scope/request validation.
2. Existing replay lookup.
3. Replay comparison resolves blank incoming lot against existing DocumentNo.
4. For new request, existing DocumentNo allocation.
5. Resolve blank OutputLotNo to allocated DocumentNo.
6. Persist output.
7. Persist existing material facts/link.
8. Commit.
9. Existing POST/ROLLBACK/cost path unchanged.

## Finished Good

1. Existing scope/date/line validation.
2. New: existing Inventory BatchNo allocation; Edit: load existing batch.
3. Resolve blank RefNo to existing/new BatchNo.
4. Existing source/lot/destination validation.
5. Save draft.
6. Existing POST/ROLLBACK/costing unchanged.

# Invariants

- typed user value is not overwritten;
- blank auto-capable value does not fail merely for being blank;
- system transaction identities remain auto/read-only;
- Inventory BatchNo generation is unchanged;
- Daily Production DocumentNo generation is unchanged;
- FG BatchNo generation is unchanged;
- source lot is never auto-replaced;
- Stock Transfer source lot still matches selected balance;
- FG blank destination lot still inherits source production lot;
- POST never generates/replaces these values;
- ROLLBACK never generates/replaces these values;
- replay of the same Daily Production blank-lot request remains idempotent;
- costing/valuation authority is unchanged;
- posted/historical records are unchanged.

# Rollback / Reversal

No rollback/reversal business logic change.

MUST preserve:

```text
POST uses identifiers already persisted on draft
ROLLBACK uses/reverses existing posting evidence
no identifier regeneration during reversal
```

Do not change:

- Inventory posting/rollback paths;
- `ProductionOutputService.Rollback.cs`;
- `ProductionFinishedGoodReceiptService.Posting.cs`;
- posting IDs;
- movement IDs;
- valuation facts;
- source fingerprints;
- reversal linkage.

# Concurrency / Idempotency

No new locking architecture.

Preserve:

- `IRunningNumberService` concurrency;
- branch/company locks;
- EF transaction boundaries;
- row-version checks;
- posting coordinator;
- `PostingRequestId` replay protection.

Critical Daily Production rule:

```text
ReplayPayloadMatches MUST compare the resolved semantic OutputLotNo,
not the raw blank request.
```

# Tests

## Daily Production

File:

`ErpWeb.Tests/Production/Transaction/ProductionOutputEntryServiceTests.cs`

Add:

### `Create_blank_output_lot_uses_document_number`

Assert:

```text
Create succeeds
saved OutputLotNo == saved DocumentNo
database row OutputLotNo == DocumentNo
```

### `Create_manual_output_lot_is_preserved`

Assert trimmed manual value persists.

### `Create_blank_output_lot_replay_is_idempotent`

Use the same valid `PostingRequestId` and the same request with blank OutputLotNo twice.

Assert:

```text
both calls succeed
same UID
one ProductionOutput row
one ProductionPostingLink row
```

### `Create_manual_output_lot_replay_with_blank_is_rejected`

First save with manual lot, retry same PostingRequestId with blank.

Assert replay is rejected as different payload.

### `Update_blank_output_lot_uses_existing_document_number`

Create NEW record, update with blank lot, assert `OutputLotNo == existing DocumentNo`.

### `Output_lot_over_50_chars_is_validation_error`

Assert:

```text
no ProductionOutput persisted
validation failure returned
```

Keep existing replay/material/sequence tests unchanged.

## Finished Good

File:

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

Add:

### `Blank_reference_defaults_to_batch_number`

Assert:

```text
saved RefNo == BatchNo.ToString(InvariantCulture)
```

### `Whitespace_reference_defaults_to_batch_number`

Use whitespace-only input and assert same fallback.

### `Manual_reference_is_trimmed_and_preserved`

Assert manual value persists.

Keep existing:

- `Lot_controlled_source_defaults_and_keeps_destination_lot`;
- posting/rollback tests;
- failure atomicity;
- cost restoration;
- cross-branch lot ownership.

## Inventory regression

Run unchanged:

- `ErpWeb.Tests/Inventory/Transaction/IvGoodsReceiptServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvMiscReceiptServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockReturnServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs`
- relevant Inventory `*PostingServiceTests.cs`
- relevant Inventory `*LedgerAuthorityTests.cs`
- `ErpWeb.Tests/Inventory/Master/IvLotNumberGeneratorTests.cs`

Do not weaken any service-side blank-lot defensive validation tests.

# Required Build / Test Commands

Run from repository root.

## Build

```bash
dotnet build ErpWeb.slnx
```

## Focused Production

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~ProductionOutputEntryServiceTests|FullyQualifiedName~FinishedGoodReceiptTests"
```

## Inventory regression

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Inventory&Category!=SqlServer"
```

## Production regression

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Production&Category!=SqlServer"
```

## Fast full regression

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"
```

SQL Server-specific suites remain required in the normal environment/CI where the configured scratch SQL Server is available. Do not silently rewrite or skip failing SQL Server tests because of this feature.

# Implementation Order

**STEP 1** — add Daily Production replay/fallback tests and FG RefNo tests.

**STEP 2** — Inventory Ref No UI defaults/placeholders only.

**STEP 3** — Goods Receipt, Misc Receipt, Stock Return lot UX.

**STEP 4** — Stock Transfer shared async destination-lot generator and blank commit path.

**STEP 5** — Daily Production shared OutputLot resolver, max-length validation, Create/Update/replay normalization.

**STEP 6** — FG RefNo normalization/fallback and FG lot wording.

**STEP 7** — focused tests.

**STEP 8** — Inventory + Production regression.

**STEP 9** — full build + non-SQL Server full regression.

**STEP 10** — inspect diff and confirm no prohibited files/logic changed.

# Regression Areas

MUST remain unchanged:

- Inventory posting;
- Inventory rollback;
- NEW draft delete;
- stock ledger;
- FIFO;
- Moving Average;
- Standard Cost;
- inventory cost state;
- valuation facts;
- period close;
- future-stock/date validation;
- Production material consumption;
- Production WIP valuation;
- Daily Production POST/ROLLBACK;
- FG POST/ROLLBACK;
- FG production-pool depletion;
- Sales;
- Procurement;
- e-Invoice;
- Work Order release/reopen;
- source-document relationships;
- audit/history.

# Do-Not Rules

DO NOT:

- edit Inventory posting logic;
- edit Inventory rollback logic;
- edit `ProductionOutputService.Posting.cs`;
- edit `ProductionOutputService.Rollback.cs`;
- edit `ProductionFinishedGoodReceiptService.Posting.cs`;
- modify costing/valuation/ledger/FIFO/MA/Standard Cost/month-end logic;
- change Inventory BatchNo;
- change Daily Production DocumentNo;
- change Work Order number;
- make Sales/Purchase document identities manually editable;
- add numbering tables/sequences;
- add schema/migrations;
- auto-generate source lots;
- auto-generate legal/external identifiers;
- weaken service-side defensive validation;
- generate identifiers during POST/ROLLBACK;
- change posted/historical records;
- globally replace every `"AUTO"` string;
- change `BatchNoDisplay = "AUTO"`;
- perform unrelated refactoring;
- weaken tests to make implementation pass.

# Allowed Diff Guard

Before completion, inspect:

```bash
git diff --name-only
git diff
```

Every changed production file MUST be listed in this plan.

No posting/rollback/costing/ledger file may appear in the diff except:

```text
ProductionOutputService.Materials.cs
```

and in that file the only permitted business change is the `ReplayPayloadMatches(...)` OutputLotNo semantic comparison. Material calculation code in that file MUST remain unchanged.

# Acceptance Criteria

- [ ] Inventory New Ref No fields are blank with `Auto-generated if blank`.
- [ ] `BatchNoDisplay = "AUTO"` remains unchanged.
- [ ] Blank Inventory Ref No persists BatchNo through existing services.
- [ ] Typed Inventory Ref No persists unchanged.
- [ ] Goods Receipt blank lot is not visually mandatory.
- [ ] Misc Receipt blank destination lot auto-fills before validation.
- [ ] Stock Return blank destination lot auto-fills before validation.
- [ ] Stock Transfer blank destination lot uses its existing DB-aware allocator.
- [ ] Stock Transfer destination lot no longer shows required CSS.
- [ ] Typed destination lots are preserved.
- [ ] Source-lot rules remain unchanged.
- [ ] Daily Production blank OutputLotNo persists DocumentNo.
- [ ] Daily Production typed OutputLotNo is preserved.
- [ ] Daily Production blank-lot replay is idempotent.
- [ ] Daily Production manual-lot replay with changed semantic payload is rejected.
- [ ] Daily Production OutputLotNo > 50 fails validation before persistence.
- [ ] FG blank/whitespace RefNo persists BatchNo.
- [ ] FG manual RefNo is trimmed/preserved.
- [ ] FG destination-lot blank behavior still inherits production source lot.
- [ ] no schema change.
- [ ] no posting logic change.
- [ ] no rollback logic change.
- [ ] no costing/valuation/ledger change.
- [ ] focused tests pass.
- [ ] Inventory regression passes.
- [ ] Production regression passes.
- [ ] fast full regression passes.
- [ ] solution builds.
- [ ] git diff contains no unrelated change.

# Approval Status

**APPROVED FOR IMPLEMENTATION — 10/10 CODE-AGENT READY**
