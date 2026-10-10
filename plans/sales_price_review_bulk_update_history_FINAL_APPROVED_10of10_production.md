# Sales Price Review, Bulk Update & Historical Pricing
## FINAL APPROVED FOR IMPLEMENTATION — AI Code Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `production`  
**Repo baseline re-verified:** `6b729eb24be8f00e3b02ba200cb2e02e920d1e05`  
**Review date:** 2026-10-09  
**Plan status:** **APPROVED FOR IMPLEMENTATION**  
**Review score:** **10 / 10 — repo-verified implementation plan**  
**Implementation owner:** AI Coding Agent  
**Business scope:** Practical Malaysian SME sales-price review, controlled bulk price updates, future-effective price-list revisions, immutable master-price history, and improved actual selling-price traceability.

---

# 0. Final review verdict

This plan is approved against the current `production` branch.

The implementation must **extend the current pricing architecture**, not replace it.

The final design keeps three concepts strictly separate:

1. **Master / official price**
   - `IvStockMaster.SellingPrice`
   - `IvCustPrice.SellingPrice`
   - `SaItemCust.UnitPrice`

2. **Runtime resolved price**
   - the current `SaCompanyPriceMethod` + `SaItemFamilyPriceResolver` pipeline
   - `ResolveLinePricingAsync`
   - `PriceInquiry`

3. **Actual historical selling price**
   - POSTED invoice lines in the existing Sales Price History inquiry

The new feature adds:

- a **Price Review & Update** workbench;
- an immutable **Price Change History** ledger;
- future-effective price-list revision support;
- automatic audit capture when existing master screens change a selling price;
- price provenance columns in the existing POSTED-invoice Sales Price History.

No second pricing engine is permitted.

---

# 1. Important corrections made during final repo review

The earlier plan was directionally correct, but the final review found implementation details that had to be corrected before approval.

## F1 — Corrected price-list concurrency assumption

Current file:

`ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.cs`

Current `SaveCustPriceGroupAsync`:

- validates the full submitted line set;
- uses the price-group header `RowVersion` as the aggregate concurrency gate;
- starts a normal `BeginTransactionAsync(...)`;
- does **not** currently start `IsolationLevel.Serializable`.

Therefore the implementation must **not** claim that the current price-list aggregate already has Serializable overlap protection.

For the new bulk price-list Apply path:

- use the price-group header RowVersion;
- re-query affected data inside the transaction;
- update the header `ModifiedDate/ModifiedBy` so its RowVersion changes whenever its price lines are changed;
- for future date/band surgery use `IsolationLevel.Serializable`;
- catch deadlock / serialization / lock-timeout conflicts using the existing
  `ErpWeb.Core.Services.SqlErrorClassifier.IsSerializationConflict(...)`;
- return `IvMasterErrorCode.Concurrency`, never an unhandled 500.

The implementation pattern should mirror the existing Serializable handling used by:

`SaSalesRefService.ItemFamily.DisGroupItem.cs`

Do not silently retry a lost race.

---

## F2 — New Price Change History is company-scoped, not branch-scoped

Pricing masters are company-scoped.

The runtime pricing contract deliberately does not put `BranchCode` into a price key.

The existing:

`ISaSalesInquiryService`

is designed for operational sales-document inquiries and its gate resolves the authenticated branch.

Therefore **do not put the new master Price Change History inside `SaSalesInquiryService`**.

Create a separate company-scoped read service:

```text
ISaPriceChangeHistoryService
SaPriceChangeHistoryService
```

It must resolve:

`ITenantScopeContext.TryCompanyScope()`

and filter only by the authenticated `CompanyCode`.

This avoids incorrectly hiding company-wide price changes from a user merely because they are signed into one branch.

The existing POSTED-invoice Sales Price History remains branch-scoped exactly as it is today.

---

## F3 — Future price-list scheduling is now deterministic

Do not use a vague rule such as:

> if future date then close whatever current row looks applicable.

The final contract is:

- the user first loads an explicit existing price-list row;
- the row carries its `IvCustPrice.Id`;
- future scheduling may only split **that exact selected row**;
- the requested effective date must fall inside that selected row's existing validity window;
- if another scheduled row already owns that future date, block and ask the user to reload the price list using that future date;
- never automatically jump from one stored row to another;
- never repair an already ambiguous timeline automatically.

This makes future revisions predictable and auditable.

---

## F4 — Master audit coverage is exact, not "where practical"

History must be written from all official price-maintenance paths in scope:

1. new Price Review & Update workbench;
2. `IvStockMasterService.SaveAsync`;
3. `IvStockMasterService.DeleteAsync` when a deleted item had a selling price;
4. `SaSalesRefService.SaveItemCustAsync`;
5. `SaSalesRefService.DeleteItemCustsAsync`;
6. `SaSalesRefService.SaveCustPriceGroupAsync`, including line add/update/remove.

A Price Group header delete does not need a price event because the current delete contract blocks deleting a header while `IvCustPrice` lines still exist.

---

## F5 — Audit detects commercial price semantics, not only number changes

The audit is not limited to:

`100.00 -> 105.00`.

The following can also change what a price means:

- selling UOM;
- currency;
- quantity band;
- validity window.

Therefore the history detail keeps before/after values for the pricing-defining fields.

Example:

`RM100 / PCS` becoming `RM100 / BOX`

must not disappear from history merely because the number did not change.

---

## F6 — Historical analysis gets lightweight snapshots

The history table must survive later master changes/deletes.

Store lightweight descriptive snapshots needed for later analysis:

- Item description;
- Item Type;
- Item Class;
- Item Subclass;
- Brand;
- for Customer Special: Customer Name, Customer Type, Customer Group.

Do not rely only on today's master classifications when later analyzing a 2026 price revision.

---

## F7 — Import/export is tied to a stable review key

Do not trust workbook key columns as permission to create/update records.

Each exported review row has an immutable `ReviewRowKey`:

```text
ITEM_DEFAULT|<ItemCode>

PRICE_LIST|<CustPriceCode>|<IvCustPrice.Id>

CUSTOMER_ITEM|<CustCode>|<ItemCode>|<UOM>|<MOQ>
```

Import may change only:

- `New Price`;
- `Selected`.

The imported key must already exist in the current server-side staged review.

Apply still re-queries the database and revalidates concurrency.

---

## F8 — Existing Sales Price History keeps its current access semantics

Current `/sales/inquiry/price-history` already displays Unit Price / Net Unit Price under its existing inquiry ACCESS gate.

This task must **not** introduce a breaking permission change to that existing inquiry.

Enhance it with persisted provenance fields, but keep its current access pattern.

The new master Price Change History gets its own stricter:

- ACCESS
- VIEW_PRICE
- EXPORT

permissions.

---

# 2. Current repo facts — implementation authority

## 2.1 Runtime pricing method

File:

`ErpWeb.Core/Sales/SaCompanyPriceMethod.cs`

Current company modes:

```text
CUSTOMER_ITEM_AND_LIST
CUSTOMER_ITEM_ONLY
PRICE_LIST_ONLY
ITEM_DEFAULT_ONLY
```

Full specificity order:

```text
1. CustomerItem
2. CustomerPriceList
3. CustomerGroupPriceList
4. ItemDefault
```

**Do not reorder it.**

**Do not make it user-configurable by priority.**

The company method only decides which existing sources are eligible.

---

## 2.2 Current price sources

### Item Default

Entity:

`ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`

Price:

`SellingPrice`

Relevant filter fields already available:

- `IType`
- `IClassCode`
- `ISubClassCode`
- `Brand`
- `SellingUom`
- `IsActive`

Concurrency:

`RowVersion`

---

### Price List

Entities:

```text
IvCustPriceGroup
IvCustPrice
```

Price line already supports:

- item;
- UOM;
- selling price;
- pack size;
- MinQty;
- MaxQty;
- ValidFrom;
- ValidTo;
- CurrencyCode.

Existing unique index:

```text
(CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, MinQty, CurrencyCode)
```

Existing ambiguity rule:

Two rows are ambiguous only when, for the same item/UOM/currency:

- their quantity bands overlap; AND
- their date windows overlap.

Shared underlying helpers:

```text
SaItemFamilyRuleMatch.BandsOverlap
SaItemFamilyRuleMatch.WindowsOverlap
```

Concurrency owner:

`IvCustPriceGroup.RowVersion`

`IvCustPrice` itself has no RowVersion.

---

### Customer Special / Negotiated

Entity:

`SaItemCust`

Business key:

```text
CompanyCode
CustCode
ICode
SellingUOM
MOQ
```

Price:

`UnitPrice`

Important:

Database/runtime legacy representation is `float` / `double?`.

Current service intentionally converts through:

```text
ScaleLegacyMoney
UnscaleLegacyMoney
```

The new workbench and audit must preserve that boundary.

Never compare customer-item price history using raw binary double equality.

Compare the same scaled `decimal(18,4)` representation used by the existing service.

---

# 3. Locked business design

## D1 — no new runtime price levels

The following are **filters**, not runtime price sources:

- Item Type
- Item Class
- Item Subclass
- Brand
- Customer
- Customer Type
- Customer Group
- Price List

Do not create:

- `CustomerTypePrice`;
- `ItemClassPrice`;
- arbitrary priority rules;
- SAP-style condition records.

---

## D2 — the three maintenance targets

The workbench supports:

```text
ITEM_DEFAULT
PRICE_LIST
CUSTOMER_ITEM
```

Labels:

```text
Item Default
Price List
Customer Special
```

---

## D3 — approved price adjustment methods

Exactly:

```text
SET_PRICE
INCREASE_PERCENT
DECREASE_PERCENT
INCREASE_AMOUNT
DECREASE_AMOUNT
```

No arbitrary formula scripting in v1.

---

## D4 — Customer Special is update-existing-only

Bulk Customer Special maintenance may update existing `SaItemCust` rows.

It must not manufacture missing combinations.

No Cartesian creation from:

`Customer Type x Item Class`

or similar filters.

---

## D5 — Price List is the shared/group pricing vehicle

If many customers share pricing, use the existing Price List model.

Customer Group may be used to resolve the group's:

`SaCustGroup.CustPriceCode`

but the target written is still:

`IvCustPrice`.

Do not duplicate shared prices into every customer.

---

## D6 — Item Default and Customer Special are immediate-only

Do not add effective-date columns to:

- `IvStockMaster`;
- `SaItemCust`.

Only Price List supports future scheduling in this implementation.

---

# 4. New UI — Price Review & Update

## 4.1 Navigation

New menu:

**Sales > Masters > Price Review & Update**

Route:

`/sales/pricing/review`

Menu code:

`SA_PRICE_MAINTENANCE`

Recommended Sales Masters sort order:

`22`

after current Price Inquiry (`21`).

This is a pricing workbench, not a Sales transaction document.

Do not use:

`SaDocPage`.

Use the existing:

- `iv-page`;
- `iv-hero`;
- `iv-card`;
- DevExpress controls;
- repository lookup components.

Mandatory UI engineering reference:

`.agents/skills/erp-ui-core/skill.md`

Also inspect before coding:

```text
ErpWeb.UI/Sales/Masters/SaCustPriceGroupList.razor(.cs/.css)
ErpWeb.UI/Sales/Masters/SaItemCustList.razor(.cs)
ErpWeb.UI/Sales/PriceInquiry.razor(.cs)
.agents/skills/erp-large-data-smart-lookup-standard/skill.md
```

---

# 5. Workbench flow

The page uses this explicit flow:

```text
1. Choose Target
2. Set Scope Filters
3. Load Prices
4. Choose Adjustment
5. Calculate
6. Review / edit New Price
7. Preview
8. Enter Reason
9. Confirm
10. Apply
11. Show Batch Reference
```

No DB write occurs before step 10.

---

# 6. Filter rules

## 6.1 Common item filters

All targets:

- Item;
- Item Type;
- Item Class;
- Item Subclass;
- Brand;
- Active Item only.

Subclass lookup must depend on selected class.

Do not load an unbounded item list into Blazor merely to filter in memory.

---

## 6.2 Item Default filters

Source:

`IvStockMaster`

Allowed:

- Item
- Item Type
- Item Class
- Item Subclass
- Brand
- Active Item only

Customer controls are hidden.

---

## 6.3 Price List filters

Source:

`IvCustPriceGroup + IvCustPrice + IvStockMaster`

Required:

- explicit Price List; OR
- Customer Group that has one `CustPriceCode`.

If Customer Group has no default Price List:

block:

> Customer Group X has no default price list. Select a Price List directly.

Additional filters:

- Item
- Item Type
- Item Class
- Item Subclass
- Brand
- UOM
- Currency
- `Review As Of` date

`Review As Of` defaults to:

`ICurrentDateService.Today`

Only lines whose validity window contains the Review As Of date are loaded.

Quantity tiers remain separate rows.

---

## 6.4 Customer Special filters

Source:

`SaItemCust + SaCust + IvStockMaster`

Allowed:

- Customer
- Customer Type
- Customer Group
- Active Customer only
- Item
- Item Type
- Item Class
- Item Subclass
- Brand
- UOM
- MOQ

Only existing `SaItemCust` rows can enter the review.

---

# 7. Anti-accident scope requirement

A user may not open the page and immediately update every company price by default.

Before Load Prices require at least one meaningful filter:

```text
Item
Item Type
Item Class
Item Subclass
Brand
Price List
Customer
Customer Type
Customer Group
```

For Item Default only, support an explicit checkbox:

`Load all active items`

When selected:

- show a warning;
- require confirmation before load;
- never default it on.

---

# 8. Review row ceiling

Set:

```text
MaxReviewRows = 5000
```

Before materializing:

- count the matching rows;
- if count > 5000, return a validation result asking the user to narrow filters.

Do not fetch 5,001 rows just to discover the limit if the query can count first.

This protects:

- Blazor Server memory;
- SQL parameter limits;
- accidental company-wide changes;
- unusable grids.

---

# 9. Price adjustment calculator

Recommended file:

`ErpWeb.Core/Sales/SaPriceAdjustmentCalculator.cs`

Pure function.

No DB.

No tenant context.

Use `decimal` only.

## 9.1 SET_PRICE

```text
new = value
```

Allowed when old price is null.

---

## 9.2 INCREASE_PERCENT

```text
new = old * (1 + value / 100)
```

Old price required.

---

## 9.3 DECREASE_PERCENT

```text
new = old * (1 - value / 100)
```

Old price required.

Reject:

`value > 100`.

---

## 9.4 INCREASE_AMOUNT

```text
new = old + value
```

Old price required.

---

## 9.5 DECREASE_AMOUNT

```text
new = old - value
```

Old price required.

Reject a negative result.

---

# 10. Rounding contract

Supported precision:

```text
2 decimals
4 decimals
```

Supported direction:

```text
NORMAL
UP
DOWN
```

For non-negative prices:

### NORMAL

```csharp
decimal.Round(value, places, MidpointRounding.AwayFromZero)
```

### UP

Conceptually:

```text
ceiling(value * scale) / scale
```

### DOWN

Conceptually:

```text
floor(value * scale) / scale
```

Perform rounding once, after adjustment.

Do not use repeated intermediate rounding.

---

# 11. Null / zero behavior

A missing price is not RM0.

Rules:

- SET_PRICE may populate a null price.
- percent methods require old price.
- amount methods require old price.
- a row with null old price under an incompatible method is blocked.
- explicit New Price = 0 is permitted.
- no code may translate null to zero automatically.

This preserves the current pricing engine's fail-closed philosophy.

---

# 12. Review grid

Use DevExpress `DxGrid`.

Columns must be readable and resizable.

Common:

```text
Select
Item
Description
UOM
Item Type
Item Class
Current Price
Calculated / Adjustment
New Price
Difference Amount
Difference %
Status / Warning
```

Price List adds:

```text
Price List
Min Qty
Max Qty
Valid From
Valid To
Currency
```

Customer Special adds:

```text
Customer
Customer Name
Customer Type
Customer Group
MOQ
Currency
```

`New Price` must be directly editable.

Do not force one popup per row.

Keyboard workflow:

- Enter commits;
- Tab proceeds;
- selection stays stable.

---

# 13. Server review identity

Each row carries a stable internal key.

## Item Default

```text
ITEM_DEFAULT|<ICode>
```

Concurrency:

`IvStockMaster.RowVersion`

---

## Price List

```text
PRICE_LIST|<CustPriceCode>|<IvCustPrice.Id>
```

Concurrency:

`IvCustPriceGroup.RowVersion`

Also carry:

`IvCustPrice.Id`

---

## Customer Item

```text
CUSTOMER_ITEM|<CustCode>|<ICode>|<SellingUOM>|<MOQ>
```

Concurrency:

`SaItemCust.RowVersion`

Do not render RowVersion.

---

# 14. Calculate

`Calculate New Prices`:

- updates staged rows only;
- does not call SaveChanges;
- fills Proposed/New Price;
- produces warnings;
- keeps selected state;
- allows manual New Price edits afterwards.

---

# 15. Preview

Preview is mandatory.

Server Preview must revalidate:

- user permissions;
- company scope;
- row identities;
- row count;
- new prices;
- target-specific rules.

Preview summary:

```text
Matched
Selected
Changing
Unchanged
Blocked
Items affected
Customers affected
Price lists affected
Minimum RM change
Maximum RM change
Average % change where meaningful
```

Apply is disabled while any selected row is blocked.

---

# 16. Bulk reason

For workbench Apply:

Reason is mandatory.

Maximum:

`200`

Examples:

```text
2027 Annual Price Review
Supplier cost increase Oct 2026
Dealer price realignment
Promotion ended
Management approved adjustment
```

Existing single-record master screens do not become reason-mandatory in this phase.

---

# 17. Price List update modes

For Price List target, use two explicit modes.

## Mode A — Update Selected Row

Meaning:

Change `SellingPrice` on the exact selected `IvCustPrice` row.

Preserve:

- ValidFrom;
- ValidTo;
- UOM;
- MinQty;
- MaxQty;
- CurrencyCode;
- SellPackSize.

If the selected row is future-dated, this updates the already scheduled price row.

Do not call this "effective immediately" because the row's own validity window remains authoritative.

---

## Mode B — Schedule From Date

Available only for Price List.

Requires:

```text
Effective From >= ICurrentDateService.Today
```

No past scheduling.

Do not rewrite historical windows through the bulk workbench.

---

# 18. Deterministic future scheduling algorithm

For each selected Price List row:

1. Re-query exact `IvCustPrice.Id`.
2. Verify:
   - same company;
   - same CustPriceCode;
   - header RowVersion unchanged;
   - baseline price unchanged.
3. Let:
   - `sourceFrom = row.ValidFrom`
   - `sourceTo = row.ValidTo`
   - `effective = request.EffectiveFrom.Date`
4. Require the selected row to contain `effective`:

```text
sourceFrom <= effective
AND
(sourceTo IS NULL OR effective <= sourceTo)
```

5. If not contained:
   - block;
   - do not jump to another row;
   - message:
     > The selected price row is not effective on YYYY-MM-DD. Reload Price List using that Review As Of date.
6. If `effective == sourceFrom`:
   - do not split;
   - update this row's price;
   - history ChangeKind = `SCHEDULE`.
7. If `effective > sourceFrom`:
   - capture original window;
   - set existing row:
     `ValidTo = effective - 1 day`
   - create successor row:
     - same price list;
     - same item;
     - same UOM;
     - same description;
     - same pack size;
     - same MinQty;
     - same MaxQty;
     - same CurrencyCode;
     - `ValidFrom = effective`;
     - `ValidTo = original sourceTo`;
     - new selling price.
8. Build the complete prospective line set for the affected price list.
9. Run the same shared band/window ambiguity validator used by manual Price Group maintenance.
10. If any ambiguity exists:
    - rollback;
    - report conflict;
    - never auto-repair.
11. Save source row + successor + header RowVersion bump + history in one transaction.

This preserves the old price for prior document dates.

---

# 19. Shared price-list overlap validator

Current overlap logic is private inside:

`SaSalesRefService.ItemFamily.cs`.

Extract the pure comparison into an internal shared helper, for example:

`ErpWeb.Core/Sales/SaPriceListOverlapValidator.cs`

Requirements:

- use `SaItemFamilyRuleMatch.BandsOverlap`;
- use `SaItemFamilyRuleMatch.WindowsOverlap`;
- currency is part of the overlap identity;
- no behavior change to existing manual Price Group validation;
- existing tests stay green.

Both:

- `SaveCustPriceGroupAsync`;
- new `SaPriceMaintenanceService`;

must call the same pure validator.

Do not create two implementations.

---

# 20. Transaction/isolation policy

## 20.1 Item Default

Use normal transaction/read-committed behavior plus:

`IvStockMaster.RowVersion`

for every selected row.

No Serializable range lock required.

---

## 20.2 Customer Special

Use normal transaction/read-committed behavior plus:

`SaItemCust.RowVersion`

for every selected row.

No Serializable range lock required.

---

## 20.3 Price List

For workbench Apply:

```text
IsolationLevel.Serializable
```

Inside the transaction:

1. load affected headers in deterministic `CustPriceCode` order;
2. verify all header RowVersions;
3. load all affected line sets;
4. validate baseline prices;
5. construct the complete proposed line sets;
6. run overlap validator;
7. mutate lines;
8. update each header `ModifiedDate/ModifiedBy` to force RowVersion change;
9. add history;
10. SaveChanges;
11. commit.

Catch:

- `DbUpdateConcurrencyException`;
- duplicate key;
- `SqlErrorClassifier.IsSerializationConflict`.

Any one failure rolls back the whole batch.

---

# 21. Current-price stale check

RowVersion is mandatory but also compare staged baseline price to current persisted price.

For `SaItemCust`, compare scaled decimal.

Example error:

> ITEM001 price changed from RM100.00 to RM103.00 after this review was loaded. Reload prices before applying.

Never overwrite a newer user review.

---

# 22. New historical pricing tables

Add:

```text
ErpWeb.Model/Entities/Sales/SaPriceChangeBatch.cs
ErpWeb.Model/Entities/Sales/SaPriceChangeLine.cs
```

Configurations:

```text
ErpWeb.Model/Configurations/Sales/SaPriceChangeBatchConfiguration.cs
ErpWeb.Model/Configurations/Sales/SaPriceChangeLineConfiguration.cs
```

DbSets:

`AppDbContext`

No FK to live Item, Customer, Price List, or Price Line masters.

Only Line -> Batch relationship.

Use `DeleteBehavior.Restrict/NoAction`.

The history must remain readable if a live master is later deleted.

---

# 23. SaPriceChangeBatch final schema

Recommended:

```text
PriceChangeBatchId       bigint identity PK
CompanyCode              nvarchar(5)  not null

Origin                    nvarchar(40) not null
TargetType                nvarchar(30) not null
AdjustmentMethod          nvarchar(30) null
AdjustmentValue           decimal(18,4) null
RoundingMode              nvarchar(20) null
DecimalPlaces             int null

EffectiveDate             date not null
Reason                    nvarchar(200) null

ItemSearchFilter          nvarchar(100) null
ItemTypeFilter            nvarchar(20) null
ItemClassFilter           nvarchar(20) null
ItemSubClassFilter        nvarchar(20) null
BrandFilter               nvarchar(50) null

CustCodeFilter            nvarchar(20) null
CustTypeFilter            nvarchar(20) null
CustGroupFilter           nvarchar(20) null
CustPriceCodeFilter       nvarchar(20) null
CurrencyFilter            nvarchar(5) null
UomFilter                 nvarchar(10) null

ChangedRowCount           int not null

ChangedAtUtc              datetime2 not null
ChangedBy                 nvarchar(10) not null
```

Do not store BranchCode as history ownership.

Price policy is company-wide.

`ChangedAtUtc` is an audit instant.

`EffectiveDate` is a company-local commercial date.

---

# 24. Batch origin tokens

Create fixed constants:

```text
PRICE_REVIEW_WORKBENCH
ITEM_MASTER
PRICE_LIST_MASTER
CUSTOMER_ITEM_MASTER
```

Do not store page display captions as protocol values.

---

# 25. Adjustment method tokens

Besides the five workbench methods, allow audit-only:

```text
DIRECT_EDIT
DELETE
```

`DIRECT_EDIT` is for existing master screens.

---

# 26. SaPriceChangeLine final schema

Recommended fields:

```text
PriceChangeLineId           bigint identity PK
PriceChangeBatchId          bigint not null

ChangeKind                  nvarchar(20) not null

ItemCode                    nvarchar(30) not null
ItemDescriptionSnapshot     nvarchar(200) null
ItemTypeSnapshot            nvarchar(20) null
ItemClassSnapshot           nvarchar(20) null
ItemSubClassSnapshot        nvarchar(20) null
BrandSnapshot               nvarchar(50) null

CustCode                    nvarchar(20) null
CustomerNameSnapshot        nvarchar(200) null
CustomerTypeSnapshot        nvarchar(20) null
CustomerGroupSnapshot       nvarchar(20) null

CustPriceCode               nvarchar(20) null
PriceListDescriptionSnapshot nvarchar(50) null
SourcePriceListLineId       int null

Moq                         int null

OldUom                      nvarchar(10) null
NewUom                      nvarchar(10) null

OldCurrencyCode             nvarchar(5) null
NewCurrencyCode             nvarchar(5) null

OldMinQty                   decimal(18,4) null
OldMaxQty                   decimal(18,4) null
NewMinQty                   decimal(18,4) null
NewMaxQty                   decimal(18,4) null

OldValidFrom                date null
OldValidTo                  date null
NewValidFrom                date null
NewValidTo                  date null

OldPrice                    decimal(18,4) null
NewPrice                    decimal(18,4) null
```

Do not duplicate ChangedBy/ChangedAtUtc on every line.

They come from the batch.

---

# 27. ChangeKind tokens

Use:

```text
CREATE
UPDATE
DELETE
SCHEDULE
```

### CREATE

No previous priced master row.

### UPDATE

Existing commercial pricing semantics changed.

### DELETE

Priced master row was removed.

### SCHEDULE

Future Price List revision created/updated.

---

# 28. What counts as a pricing-semantic change

## Item Default

Audit when:

- SellingPrice changes; OR
- SellingUom changes while a selling price exists.

---

## Customer Item

Audit when:

- UnitPrice changes after normalizing to decimal(18,4); OR
- Currency changes while a unit price exists.

The key UOM/MOQ cannot be changed through the current update contract.

---

## Price List

Audit when any of these changes:

- SellingPrice;
- UOM;
- MinQty;
- MaxQty;
- ValidFrom;
- ValidTo;
- CurrencyCode.

Pack size is advisory in the current resolver and need not create a price-history event by itself.

Description changes do not create price-history events.

---

# 29. History immutability

Application exposes:

- search;
- read;
- export.

No:

- edit;
- delete;
- deactivate;
- correction-in-place.

If a price is wrong:

make a new price change.

Do not edit old history evidence.

No RowVersion is required on append-only history.

---

# 30. Shared audit factory

Do not create a service that owns a second DbContext/transaction.

Create a pure helper in a module-neutral Core namespace, for example:

```text
ErpWeb.Core/Pricing/SalesPriceChangeAuditFactory.cs
```

Responsibilities:

- build Batch entity;
- build Line entities;
- snapshot descriptive fields;
- normalize token values;
- never SaveChanges;
- never start a transaction;
- never commit.

Caller adds the entity graph to its **existing DbContext**.

This avoids:

> price saved but audit save failed separately.

---

# 31. Audit hook — IvStockMasterService.SaveAsync

File:

`ErpWeb.Core/Inventory/IvStockMasterService.cs`

## New item

If new item has non-null SellingPrice:

create:

```text
Origin      = ITEM_MASTER
Target      = ITEM_DEFAULT
ChangeKind  = CREATE
OldPrice    = null
NewPrice    = model.SellingPrice
```

## Existing item

Capture old before `ApplyEditableFields`.

After applying fields compare:

- old/new SellingPrice;
- old/new SellingUom.

If pricing semantics changed:

add one audit batch/line to the same DbContext before SaveChanges.

If unrelated fields changed:

no price audit.

---

# 32. Audit hook — IvStockMasterService.DeleteAsync

The current delete path already:

- checks RowVersion;
- checks references;
- runs one transaction.

Before `RemoveRange`:

- build one audit batch for priced entities being deleted;
- one history line per deleted item with non-null SellingPrice;
- Origin = ITEM_MASTER;
- Target = ITEM_DEFAULT;
- ChangeKind = DELETE.

If none of the deleted items has a selling price:

no price batch.

Add audit + deletes to the same transaction.

---

# 33. Audit hook — SaveItemCustAsync

File:

`SaSalesRefService.ItemFamily.cs`

Do not add a constructor dependency merely for auditing.

Use the pure audit factory.

For existing row:

- load tracked row;
- capture scaled old price and old currency;
- mutate row;
- if pricing semantics changed, add audit graph;
- same SaveChanges.

For create with non-null price:

CREATE audit.

Manual screen Reason:

null.

AdjustmentMethod:

`DIRECT_EDIT`.

---

# 34. Audit hook — DeleteItemCustsAsync

Current Customer Item delete delegates to the shared:

`DeleteItemFamilyAsync<TEntity>`

Extend the private helper minimally with an optional callback, e.g.:

```text
beforeDelete(db, entities, context)
```

Requirements:

- callback runs only after:
  - key validation;
  - rowversion validation;
  - reference check;
- callback runs before `db.Remove(...)`;
- callback may add history entities to the same DbContext;
- default is null for existing callers;
- no behavior change to Discount Rule deletion;
- Price Group deletion does not add price audit because headers with price lines are already blocked.

`DeleteItemCustsAsync` supplies the callback.

Build one batch for the bulk delete.

---

# 35. Audit hook — SaveCustPriceGroupAsync

Current aggregate save can:

- remove line;
- update line;
- add line.

Before mutation, compare:

`storedLines`

against normalized payload.

Create one audit batch per successful aggregate Save if one or more pricing-semantic changes exist.

Examples:

```text
1 changed line  -> 1 batch, 1 detail
20 changed lines -> 1 batch, 20 details
```

Line removed:

`DELETE`

Line inserted with a price:

`CREATE`

Existing line commercial semantics changed:

`UPDATE`

No price-semantic change:

no history batch.

The history entity graph participates in the same existing transaction.

If any later save fails:

history rolls back too.

---

# 36. Price List activation / customer-list assignment

These affect price eligibility, but they are **not price amount revisions**.

This v1 Price Change History does not audit:

- `IvCustPriceGroup.IsActive` change by itself;
- `SaCust.CustPriceCode` assignment;
- `SaCustGroup.CustPriceCode` assignment.

Do not quietly pretend otherwise.

Actual Sales Price History still keeps invoice `PricingSource/PricingRef`.

A later **Pricing Assignment History** feature may audit these policy-assignment changes if business demand justifies it.

---

# 37. New maintenance service

Add:

```text
ErpWeb.Core/Sales/ISaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceContracts.cs
ErpWeb.Core/Sales/SaPriceAdjustmentCalculator.cs
ErpWeb.Core/Sales/SaPriceListOverlapValidator.cs
```

Register:

`ISaPriceMaintenanceService -> SaPriceMaintenanceService`

in:

`ErpWeb.Core/CoreServiceCollectionExtensions.cs`

Use:

`ITenantScopeContext`

for new code where possible.

Reads require:

`TryCompanyScope()`.

Future Price List inserts also require a valid write scope for stamping existing leftover Branch/Location columns consistently with current price-list creation behavior.

---

# 38. Maintenance service operations

Recommended:

```csharp
Task<IvMasterOperationResult<SaPriceReviewPage>>
    SearchAsync(SaPriceReviewQuery query, CancellationToken ct);

Task<IvMasterOperationResult<SaPricePreviewResult>>
    PreviewAsync(SaPricePreviewRequest request, CancellationToken ct);

Task<IvMasterOperationResult<SaPriceApplyResult>>
    ApplyAsync(SaPriceApplyRequest request, CancellationToken ct);

Task<IvMasterOperationResult<byte[]>>
    BuildReviewWorkbookAsync(SaPriceReviewExportRequest request, CancellationToken ct);

Task<IvMasterOperationResult<SaPriceImportPreview>>
    ParseImportAsync(Stream workbook, SaPriceImportContext context, CancellationToken ct);
```

Import parses/stages only.

It does not Apply.

---

# 39. Permission model — Price Maintenance

Menu:

`SA_PRICE_MAINTENANCE`

MenuPermission rows:

```text
ACCESS
EDIT
VIEW_PRICE
EXPORT
IMPORT
```

No ADD.

No DELETE.

No `PRICE_OVERRIDE`.

`PRICE_OVERRIDE` remains a transaction-document authority.

The new page's `EDIT` is a dedicated master-price-management authority.

Do not additionally require Inventory Item Master EDIT / Customer Item EDIT / Price Group EDIT for the bulk workbench unless the owner explicitly changes governance.

Existing master screens continue enforcing their own menus.

Server gates:

### Search

- ACCESS
- VIEW_PRICE

### Preview

- ACCESS
- VIEW_PRICE

### Apply

- ACCESS
- EDIT
- VIEW_PRICE

### Export

- ACCESS
- EXPORT
- VIEW_PRICE

### Import

- ACCESS
- IMPORT
- VIEW_PRICE

Final Apply after an import still requires EDIT.

---

# 40. Price maintenance Apply sequence

Server authoritative sequence:

```text
1. Resolve company scope.
2. Re-check menu permissions.
3. Validate target.
4. Validate required Reason.
5. Validate row count <= MaxReviewRows.
6. Validate NewPrice for every selected row.
7. Begin transaction appropriate to target.
8. Re-query exact selected rows.
9. Verify company ownership.
10. Verify rowversions / header rowversions.
11. Verify baseline prices.
12. Rebuild/check proposed changes.
13. Validate target-specific constraints.
14. Build audit batch/details.
15. Apply master changes.
16. Add audit graph.
17. SaveChanges.
18. Commit.
19. Return PRC batch reference.
```

Any selected-row failure:

**rollback the entire batch.**

No partial success mode.

---

# 41. New Price Change History service

Add:

```text
ErpWeb.Core/Sales/ISaPriceChangeHistoryService.cs
ErpWeb.Core/Sales/SaPriceChangeHistoryService.cs
ErpWeb.Core/Sales/SaPriceChangeHistoryContracts.cs
```

Do not add this query to `ISaSalesInquiryService`.

Reason:

master price history is company-scoped, not branch-scoped.

Use:

`ITenantScopeContext.TryCompanyScope()`.

Operations:

```csharp
SearchAsync(query, ct)
GetBatchAsync(batchId, query/page, ct)
ExportAsync(query, ct)
```

All methods validate their own menu rights.

---

# 42. Price Change History menu/page

Menu:

**Sales > Inquiry > Price Change History**

Route:

`/sales/inquiry/price-change-history`

Code:

`SA_PRICE_CHANGE_HISTORY`

Recommended sort:

`11`

Leave existing:

- Sales Price History = 9
- e-Invoice Status = 10

unchanged.

Do not disturb existing menu sort values unnecessarily.

---

# 43. Price Change History permissions

MenuPermission:

```text
ACCESS
VIEW_PRICE
EXPORT
```

Grid/search requires:

- ACCESS
- VIEW_PRICE

Export requires:

- ACCESS
- VIEW_PRICE
- EXPORT

Do not return zero/masked prices from a dedicated price audit inquiry.

Deny clearly if VIEW_PRICE is missing.

---

# 44. Price Change History query

Dedicated DTO, not `SaInquiryQuery`.

Suggested:

```text
ChangedDateFromUtc
ChangedDateToUtc
EffectiveDateFrom
EffectiveDateTo
Origin
TargetType
ChangeKind
ItemCode
ItemType
ItemClass
ItemSubClass
Brand
CustCode
CustType
CustGroup
CustPriceCode
ChangedBy
ReasonSearch
Skip
Take
```

Historical dimension filters should prefer stored snapshots.

Do not join today's Item Class and claim it was the historical class.

---

# 45. Price Change History grid

Columns:

```text
Batch
Changed At
Effective Date
Origin
Target
Change Kind
Item
Description
UOM
Customer
Price List
Old Price
New Price
Difference RM
Difference %
Reason
Changed By
```

Difference:

```text
NewPrice - OldPrice
```

Percent:

```text
(NewPrice - OldPrice) / OldPrice * 100
```

If OldPrice is:

- null -> Change % null
- zero -> Change % null

Never divide by zero.

---

# 46. Batch reference

Use identity:

`PriceChangeBatchId`.

Display:

```text
PRC-{Id:00000000}
```

Do not introduce another running-number race.

---

# 47. Batch details

Read-only.

Header:

- reference;
- ChangedAtUtc;
- EffectiveDate;
- Origin;
- Target;
- adjustment method/value;
- rounding;
- reason;
- filter summary;
- ChangedBy;
- row count.

Detail grid must be server-paged because one batch may contain up to 5,000 rows.

No edit.

No delete.

---

# 48. Existing Sales Price History enhancement

Keep:

`/sales/inquiry/price-history`

Current service:

`SaSalesInquiryService.GetSalesPriceHistoryAsync`

remains:

- POSTED invoice lines only;
- company + authenticated branch scoped;
- existing ACCESS semantics.

Extend `SaSalesPriceHistoryRow` with:

```text
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

Projection comes directly from:

`SaInvoiceDetail`.

Do not recompute provenance.

Grid add:

```text
Price Source
Price Ref
Original Price
Override?
Override Reason
```

`Override?`:

```text
OriginalUnitPrice != null
```

Old/legacy null provenance displays:

`—`

or:

`Not recorded`

Do not manufacture a source.

---

# 49. Why two histories stay separate

## Price Change History

Answers:

> What official/master price policy changed?

Source:

`SaPriceChangeBatch + SaPriceChangeLine`

Company-scoped.

---

## Sales Price History

Answers:

> What price did we actually sell at?

Source:

POSTED invoice line.

Branch-scoped.

---

Future analysis may compare them.

Do not merge the two datasets into one ambiguous table.

---

# 50. Export review workbook

Use existing repository spreadsheet dependencies.

Core currently already references:

- `DocumentFormat.OpenXml`;
- `ClosedXML`.

Do not add a new Excel library.

Use existing sales workbook style where practical.

Export request contains:

- target;
- filters;
- Review As Of;
- adjustment method/value;
- rounding.

Workbook:

## Meta sheet

```text
TemplateVersion = SA_PRICE_REVIEW_V1
TargetType
ReviewAsOf
ExportedAtUtc
```

## Prices sheet

Columns:

```text
ReviewRowKey
Target
Price List
Customer
Item
Description
UOM
MOQ
Min Qty
Max Qty
Currency
Valid From
Valid To
Current Price
New Price
Selected
```

`New Price` may be pre-calculated from the supplied adjustment.

---

# 51. Import review workbook

Use ClosedXML or the existing safe parser conventions in Core.

Recommended limits:

```text
MaxFileBytes = 5 MB
MaxRows      = 5000
```

Reject:

- wrong template version;
- wrong target type;
- missing headers;
- duplicate ReviewRowKey;
- formulas in `ReviewRowKey`;
- formulas in `New Price`;
- non-decimal New Price;
- negative price;
- row not present in current staged review;
- row count above cap.

Import may set only:

- `NewPrice`;
- `Selected`.

It may not change:

- item;
- customer;
- price list;
- UOM;
- MOQ;
- band;
- currency;
- validity;
- target identity.

Import never writes DB.

User must still:

Preview -> Reason -> Apply.

---

# 52. Workbench UI implementation files

Add:

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.css
```

No large inline `@code`.

No inline `<style>`.

DevExpress controls only where a repository/DevExpress widget exists.

For upload:

Blazor `InputFile` is acceptable.

Put handlers in code-behind.

---

# 53. DevExpress binding safety

Prefer:

```razor
<DxTextBox @bind-Text="@Model.Value" />
```

If using manual:

```razor
Text=
TextChanged=
```

inside a validation context, supply:

```razor
TextExpression=
```

Apply the corresponding expression concept to other DevExpress editors where required.

Do not ship the known:

`requires a value for TextExpression`

runtime error.

---

# 54. Grid usability standard

This feature is specifically for fast price maintenance.

Therefore:

- normal code columns must not be too narrow;
- Description should be readable;
- user can resize columns;
- numeric columns right aligned;
- 4-decimal price available where required;
- horizontal scroll is acceptable on small screens;
- no ellipsis on ordinary 10–20 character business codes unless column is user-resizable and initially large enough;
- New Price stays visible/useful.

---

# 55. Company date / audit time

Use two distinct concepts.

## Commercial effective date

Use:

`ICurrentDateService.Today`

company-local timezone.

## Audit instant

Use:

`DateTime.UtcNow`

stored as:

`ChangedAtUtc`.

Do not confuse the two.

---

# 56. Sales-price tax rule

Master selling prices remain tax-exclusive.

The workbench does not apply SST/tax gross-up.

Where helpful label:

`Selling Price (Tax Exclusive)`.

Tax conversion remains in the current line pricing pipeline.

---

# 57. Discount rule

This task does not alter:

- `SaDisGroupItem`;
- item discounts;
- document discounts.

Changing price does not mutate discount master.

---

# 58. Currency rule

Bulk formula changes price number only.

It never changes CurrencyCode implicitly.

If currency needs maintenance, use the existing master screen.

---

# 59. UOM rule

No automatic price conversion.

No:

`BOX -> PCS`

price derivation.

Each stored UOM row is reviewed independently.

---

# 60. Item Default warning

Show when Item Default target selected:

> Item Default is the final fallback price in the current pricing engine and may affect customers without a more specific customer/price-list price.

---

# 61. Customer Special warning

Show:

> Only existing negotiated customer-item prices will be updated. Missing customer-item prices will not be created.

---

# 62. Price List assignment impact summary

When an explicit Price List is selected, show read-only counts where practical:

```text
Customers assigned directly: N
Customer groups using as default: N
```

This is informational.

Do not block an unassigned price list.

---

# 63. Menu deployment

Modify:

`ErpWeb.Core/Menus/MenuCodes.cs`

Add:

```text
SalesPriceMaintenance      = SA_PRICE_MAINTENANCE
SalesPriceChangeHistory    = SA_PRICE_CHANGE_HISTORY
```

Modify:

`ErpWeb/Menus/menus.xml`

Add both menu rows.

`MenuDeploymentParityTests` must stay green.

---

# 64. Permission deployment

Use existing built-in Permission rows.

Do **not** invent new permission codes.

Menu seed inserts `MenuPermission` rows for existing:

### SA_PRICE_MAINTENANCE

```text
ACCESS
EDIT
VIEW_PRICE
EXPORT
IMPORT
```

### SA_PRICE_CHANGE_HISTORY

```text
ACCESS
VIEW_PRICE
EXPORT
```

If an expected Permission row is missing:

fail/print clearly according to current seed convention.

Do not silently create an incomplete menu grant set.

Role grants remain deployment-owner decisions.

If a role-grant SQL example is included, remember:

`RoleMenuPermission` uses `IsAllowed`.

Do not use `IsActive` for that table.

---

# 65. Database deployment

Add:

`scripts/init-sales-price-change-history.sql`

Add:

`scripts/init-sales-price-maintenance-menu.sql`

Optionally split history menu into same menu script if repository convention remains clear.

DDL requirements:

- additive;
- idempotent;
- safe to run twice;
- `SET XACT_ABORT ON`;
- correct `GO` batching;
- no destructive changes to existing price masters.

No automatic startup migration.

---

# 66. History indexes

Recommended practical indexes.

Batch:

```text
(CompanyCode, ChangedAtUtc DESC)
(CompanyCode, TargetType, ChangedAtUtc DESC)
(CompanyCode, EffectiveDate)
```

Line:

```text
(PriceChangeBatchId)
(ItemCode, PriceChangeBatchId)
(CustCode, PriceChangeBatchId)
(CustPriceCode, PriceChangeBatchId)
```

Do not over-index.

---

# 67. Expected new Core files

```text
ErpWeb.Core/Sales/ISaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceService.cs
ErpWeb.Core/Sales/SaPriceMaintenanceContracts.cs
ErpWeb.Core/Sales/SaPriceAdjustmentCalculator.cs
ErpWeb.Core/Sales/SaPriceListOverlapValidator.cs

ErpWeb.Core/Sales/ISaPriceChangeHistoryService.cs
ErpWeb.Core/Sales/SaPriceChangeHistoryService.cs
ErpWeb.Core/Sales/SaPriceChangeHistoryContracts.cs

ErpWeb.Core/Pricing/SalesPriceChangeAuditFactory.cs

ErpWeb.Core/Sales/SaPriceMaintenanceWorkbook.cs
```

---

# 68. Expected new Model files

```text
ErpWeb.Model/Entities/Sales/SaPriceChangeBatch.cs
ErpWeb.Model/Entities/Sales/SaPriceChangeLine.cs

ErpWeb.Model/Configurations/Sales/SaPriceChangeBatchConfiguration.cs
ErpWeb.Model/Configurations/Sales/SaPriceChangeLineConfiguration.cs
```

Update:

`ErpWeb.Model/Data/AppDbContext.cs`

---

# 69. Expected new UI files

```text
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.cs
ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor.css

ErpWeb.UI/Sales/Inquiry/SaPriceChangeHistoryInquiry.razor
ErpWeb.UI/Sales/Inquiry/SaPriceChangeHistoryInquiry.razor.cs
ErpWeb.UI/Sales/Inquiry/SaPriceChangeHistoryInquiry.razor.css
```

---

# 70. Existing files expected to change

At minimum:

```text
ErpWeb.Model/Data/AppDbContext.cs

ErpWeb.Core/CoreServiceCollectionExtensions.cs
ErpWeb.Core/Menus/MenuCodes.cs

ErpWeb.Core/Inventory/IvStockMasterService.cs

ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.cs
ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Delete.cs

ErpWeb.Core/Sales/SaInquiryResults.cs
ErpWeb.Core/Sales/SaSalesInquiryService.Workbench.cs

ErpWeb.UI/Sales/Inquiry/SaPriceHistoryInquiry.razor
ErpWeb.UI/Sales/Inquiry/SaPriceHistoryInquiry.razor.cs

ErpWeb/Sales/SaInquiryExportEndpoints.cs
ErpWeb/Program.cs

ErpWeb/Menus/menus.xml
```

Add endpoint registration only if review/history export uses new HTTP endpoints.

---

# 71. Endpoint strategy

Existing Sales export endpoints are GET downloads.

For this feature:

## History export

Use a normal GET endpoint with query filters.

It calls the same:

`ISaPriceChangeHistoryService.ExportAsync`

used to generate the grid/export dataset.

## Review export

GET may re-run the validated review query plus adjustment parameters.

Do not accept raw CompanyCode from the URL as authority.

Company always comes from tenant context.

## Import

Prefer Blazor `InputFile` -> stream -> `ISaPriceMaintenanceService.ParseImportAsync`.

No direct DB import endpoint is needed.

---

# 72. Tests — calculator

Add:

`SaPriceAdjustmentCalculatorTests`

Required:

- SET price;
- increase %;
- decrease %;
- increase amount;
- decrease amount;
- null old price;
- old zero;
- explicit new zero;
- decrease percent >100;
- negative result;
- NORMAL 2 dp;
- NORMAL 4 dp;
- UP 2 dp;
- DOWN 2 dp;
- midpoint AwayFromZero;
- large decimal.

---

# 73. Tests — filter/query

Add:

`SaPriceMaintenanceServiceTests`

## Item Default

- type;
- class;
- subclass;
- brand;
- item;
- active-only;
- over-limit count.

## Price List

- explicit list;
- customer group -> group default list;
- no group list blocks;
- Review As Of;
- UOM;
- currency;
- inactive header behavior;
- multiple qty bands remain separate.

## Customer Item

- customer;
- customer type;
- customer group;
- item class;
- UOM;
- MOQ;
- existing rows only.

---

# 74. Tests — permission

Workbench:

- ACCESS denied;
- VIEW_PRICE denied;
- EDIT denied Apply;
- EXPORT denied export;
- IMPORT denied import;
- PRICE_OVERRIDE alone does not authorize master Apply.

History:

- ACCESS denied;
- VIEW_PRICE denied;
- EXPORT denied.

Existing Sales Price History:

- current access behavior unchanged.

---

# 75. Tests — Item Default Apply

- selected rows only;
- unchanged row ignored;
- manual NewPrice wins;
- stale RowVersion rolls back whole batch;
- stale baseline rolls back;
- history created atomically;
- item SellingUom semantic change from normal master Save is audited;
- unrelated Item Master edit is not audited.

---

# 76. Tests — Customer Special Apply

- existing row update;
- no missing row creation;
- scaled decimal comparison;
- currency semantic change audited;
- stale RowVersion;
- whole-batch rollback;
- delete audit;
- CREATE audit on new manual row.

---

# 77. Tests — Price List immediate

- update exact Id only;
- preserve date/band/currency;
- header RowVersion must match;
- header gets ModifiedDate/By;
- audit UPDATE;
- manual price-list save still works;
- removal audit DELETE;
- added line audit CREATE;
- semantic change with same price is audited;
- description-only change creates no price event.

---

# 78. Tests — future Price List scheduling

Required:

1. source row contains effective date;
2. source row split correctly;
3. predecessor ends effective-1;
4. successor starts effective;
5. successor inherits original ValidTo;
6. same UOM;
7. same band;
8. same currency;
9. same pack;
10. new price correct;
11. EffectiveFrom == source ValidFrom updates row without duplicate;
12. requested date outside selected window blocks;
13. requested past date blocks;
14. existing conflicting future row blocks;
15. overlap validator is shared;
16. history SCHEDULE;
17. original/new windows recorded;
18. no partial save.

---

# 79. SQL Server concurrency tests

Add:

`SaPriceMaintenanceSqlServerConcurrencyTests`

Required:

### Item Default

Two reviews, same row:

- exactly one Apply succeeds;
- loser receives Concurrency.

### Customer Item

Same.

### Price List workbench vs workbench

Same header:

- exactly one wins.

### Price List workbench vs existing SaveCustPriceGroupAsync

- exactly one semantic result commits;
- no merged/partial line set.

### Overlapping future schedule

Two concurrent sessions:

- no two overlapping windows may commit.

### Serialization error

Expected deadlock/lock-timeout/serialization loser returns:

`Concurrency`.

### Audit

No orphan Batch/Line after rolled-back loser.

Use current scratch SQL Server safety rails.

---

# 80. Tests — audit coverage

## Item Master

- create with price;
- update price;
- UOM meaning change;
- delete priced item;
- no-op/unrelated save.

## Customer Item

- create priced row;
- update price;
- currency change;
- delete;
- float scaling no false event.

## Price List

- create priced line;
- update price;
- change band/date/currency;
- remove;
- aggregate one batch/many lines.

---

# 81. Tests — Price Change History

- company isolation;
- no branch filtering;
- ChangedAt range;
- EffectiveDate range;
- Origin;
- Target;
- ChangeKind;
- item;
- historical item class snapshot;
- customer;
- historical customer type/group snapshot;
- price list;
- ChangedBy;
- Reason search;
- server paging;
- batch details paging;
- change RM calculation;
- old zero -> null %;
- append-only service has no update/delete method;
- export cap.

---

# 82. Tests — existing Sales Price History enhancement

Extend:

`SaSalesInquiryServiceTests`

Seed posted invoice detail with:

```text
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

Assert returned exactly.

Also:

- draft excluded;
- zero qty still yields null NetUnit;
- legacy null provenance stays null;
- existing branch scoping unchanged.

Update export test if one exists.

---

# 83. Menu tests

Mandatory:

`MenuDeploymentParityTests`

must pass after new MenuCodes.

Add targeted assertions if useful that:

- new master menu has route;
- new inquiry menu has route.

Do not rely only on SQL seed.

`menus.xml` is startup authority.

---

# 84. SQL script tests/manual verification

Run each new script twice on scratch DB.

Second run:

clean no-op.

Verify:

- history tables;
- FK;
- indexes;
- menu rows;
- MenuPermission rows;
- no duplicate grants.

Do not auto-seed RoleMenuPermission universally.

---

# 85. Build/test command gate

Mandatory:

```powershell
dotnet build ErpWeb.slnx --nologo -v:q
```

Then:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --nologo
```

For SQL Server concurrency suites:

set the existing scratch connection and:

```text
ERPWEB_REQUIRE_SQLSERVER_TESTS=1
```

A green run where SQL Server tests silently skipped is not final verification.

---

# 86. Manual acceptance A — Item Class review

1. Item Default.
2. Item Class = HARDWARE.
3. Load.
4. +5%.
5. Calculate.
6. manually change one row.
7. Preview.
8. Reason.
9. Apply.
10. verify item masters.
11. verify one PRC batch.
12. verify only changed rows in history.

---

# 87. Manual acceptance B — future Dealer Price List

1. Price List.
2. `DEALER-01`.
3. Review As Of = today.
4. select rows effective today.
5. +4%.
6. Schedule From = future date inside selected row window.
7. Apply.
8. verify predecessor end.
9. verify successor start.
10. Price Inquiry before date returns old.
11. Price Inquiry on date returns new.
12. history SCHEDULE.

---

# 88. Manual acceptance C — pre-existing future row

1. Price list already has a future row starting Jan 1.
2. User loads Review As Of = today.
3. attempts Schedule From Jan 1 on today's predecessor which ends Dec 31.
4. system blocks because selected row does not contain Jan 1.
5. user reloads Review As Of = Jan 1.
6. selects future row.
7. Update Selected Row.
8. no duplicate timeline row created.

This scenario is mandatory.

---

# 89. Manual acceptance D — Customer Type special review

1. Customer Special.
2. Customer Type = WHOLESALE.
3. Item Class = PARTS.
4. +RM2.
5. only existing SaItemCust displayed.
6. Apply.
7. no new customer-item records.
8. history customer snapshots correct.

---

# 90. Manual acceptance E — existing master screens

Verify automatic history from:

- Stock Master selling price edit;
- Stock Master priced-item delete;
- Customer Item price edit;
- Customer Item delete;
- Price Group line price edit;
- Price Group line remove.

No extra bulk workbench is required for audit.

---

# 91. Manual acceptance F — actual Sales Price History

1. create Sales document using resolved price;
2. post Invoice;
3. open Sales Price History;
4. verify actual Unit Price;
5. verify Pricing Source;
6. verify Pricing Ref;
7. if overridden:
   - Original Price;
   - Override Reason.

This remains actual sales history, not master history.

---

# 92. Performance requirements

For 5,000-row review:

- count before materialization;
- one projected query per logical source, not N+1;
- no customer lookup per row;
- no item lookup per row;
- chunk large key sets below SQL Server parameter limits;
- deterministic header lock order for multi-list Apply;
- batch details server-paged;
- import capped;
- export capped.

---

# 93. Error messages

Must identify recovery action.

Examples:

> Price List DEALER-01 changed after this review was loaded. Reload before applying.

> ITEM001 has no current price. Increase % requires an existing price; use Set Price or deselect the row.

> The selected price row is not effective on 2027-01-01. Reload the price list using Review As Of 2027-01-01.

> Future price would overlap an existing quantity/date band. No changes were saved.

> Customer Group DEALER has no default price list. Select a Price List directly.

---

# 94. Logging

Use existing ILogger conventions.

Log one successful bulk Apply:

- batch id;
- company;
- target;
- changed count;
- user.

Log concurrency/serialization conflict with:

- company;
- target;
- affected list/item count.

Do not dump all price values into ordinary logs.

The DB history is the detailed evidence.

---

# 95. Explicit out of scope

Do not implement:

- central `SalesPrice` table;
- new resolver;
- configurable source order;
- customer-type runtime price;
- item-class runtime price;
- pricing assignment history;
- price-list active-state audit;
- AI price recommendation;
- cost-plus auto formula;
- elasticity prediction;
- promotion engine;
- approval workflow;
- branch price keys;
- automatic UOM conversion;
- automatic currency conversion;
- discount-rule maintenance;
- direct Invoice -> PriceChangeBatch FK;
- reconstruction of pre-deployment master history.

---

# 96. Implementation phases

## Phase 1 — shared validation + history schema

- entities/config;
- DbSets;
- tokens;
- pure calculator;
- shared overlap validator;
- audit factory;
- DDL;
- unit tests.

**Gate:** build + pure tests.

---

## Phase 2 — audit existing master paths

- Item Master save/delete;
- Customer Item save/delete;
- Price Group aggregate save.

**Gate:** existing master tests + audit tests.

Do this before building the new workbench so audit coverage is independently verified.

---

## Phase 3 — maintenance query/preview

- filters;
- count cap;
- row identities;
- calculator;
- preview;
- permissions.

**Gate:** query/permission tests.

---

## Phase 4 — Apply immediate changes

- Item Default;
- Customer Special;
- Price List Update Selected Row;
- atomic audit;
- concurrency.

**Gate:** integration + SQL Server concurrency.

---

## Phase 5 — future Price List scheduling

- split algorithm;
- Serializable path;
- conflict mapping;
- audit.

**Gate:** resolver date tests + concurrency tests.

---

## Phase 6 — workbench UI

- code-behind;
- readable grid;
- preview;
- reason;
- confirm;
- apply;
- dark/mobile;
- binding safety.

**Gate:** full solution build + manual A-D.

---

## Phase 7 — workbook export/import

- template version;
- ReviewRowKey;
- export;
- safe parse;
- staged merge;
- permissions.

**Gate:** export/import/preview/apply round-trip.

---

## Phase 8 — master Price Change History inquiry

- company-scoped service;
- list;
- details;
- export;
- menu/security.

**Gate:** history tests.

---

## Phase 9 — enhance actual Sales Price History

- persisted pricing provenance columns;
- grid/export;
- tests.

**Gate:** manual F.

---

## Phase 10 — deployment/final regression

- XML;
- scripts;
- DI;
- endpoint map;
- full build;
- full tests;
- SQL Server concurrency;
- script rerun;
- acceptance scenarios.

---

# 97. AI agent pre-code evidence block

Before editing, the coding agent must produce:

```text
SALES PRICE MAINTENANCE REPO EVIDENCE

Branch:
Commit:

SaCompanyPriceMethod modes:
Resolver source order:

IvStockMaster price/concurrency:
IvCustPrice header/line concurrency:
SaItemCust price/concurrency:

SaveCustPriceGroupAsync transaction isolation observed:
Current price-list overlap helper:
Serializable example inspected:
SqlErrorClassifier inspected:

Current Price Inquiry:
Current Sales Price History:
Current Sales Price History scope:
Current menu XML entries:
Current PermissionCodes:
RoleMenuPermission flag name:

UI core skill inspected:
Large-data lookup skill inspected:
Closest pages:

Intentional deviations from approved plan:
```

`Intentional deviations` must be:

`None`

unless a verified current-repo blocker requires a change.

---

# 98. Stop conditions

Stop and report before inventing behavior if:

1. `production` branch moved materially from this pricing architecture;
2. current pricing source order changed;
3. `IvCustPrice` identity/index changed;
4. `SaItemCust` key/legacy price type changed;
5. menu security model changed;
6. workbench cannot write audit in the same DB transaction;
7. future scheduling cannot preserve current resolver semantics;
8. an implementation would require changing Sales document re-pricing behavior.

Do not simplify away concurrency to make code compile.

---

# 99. Final Definition of Done

## Architecture

- [ ] No second pricing engine.
- [ ] Existing resolver semantics unchanged.
- [ ] Existing company pricing modes unchanged.
- [ ] Item/Customer dimensions are filters only.
- [ ] New history is company-scoped.
- [ ] Existing invoice price history stays branch-scoped.

## Workbench

- [ ] Three targets.
- [ ] Five methods.
- [ ] 2/4 dp rounding.
- [ ] readable/resizable grid.
- [ ] direct New Price entry.
- [ ] preview required.
- [ ] Reason required.
- [ ] 5,000 cap.
- [ ] no partial Apply.
- [ ] no missing Customer Item creation.

## Price List scheduling

- [ ] explicit selected row Id.
- [ ] no past scheduling.
- [ ] selected row must contain effective date.
- [ ] split correct.
- [ ] successor inherits band/UOM/currency/end.
- [ ] no automatic row jumping.
- [ ] shared overlap validator.
- [ ] Serializable workbench scheduling.
- [ ] serialization conflicts return Concurrency.

## History

- [ ] immutable Batch/Line.
- [ ] before/after price semantics.
- [ ] historical item/customer snapshots.
- [ ] Item Master create/update/delete audit.
- [ ] Customer Item create/update/delete audit.
- [ ] Price List add/update/remove audit.
- [ ] same transaction as master change.
- [ ] no fake pre-deployment history.
- [ ] no branch ownership.

## Actual selling history

- [ ] POSTED invoice only.
- [ ] PricingSource.
- [ ] PricingRef.
- [ ] OriginalUnitPrice.
- [ ] OverrideReason.
- [ ] existing access semantics preserved.
- [ ] branch scope preserved.

## Security

- [ ] SA_PRICE_MAINTENANCE XML + MenuCode.
- [ ] ACCESS.
- [ ] EDIT.
- [ ] VIEW_PRICE.
- [ ] EXPORT.
- [ ] IMPORT.
- [ ] SA_PRICE_CHANGE_HISTORY XML + MenuCode.
- [ ] ACCESS.
- [ ] VIEW_PRICE.
- [ ] EXPORT.
- [ ] PRICE_OVERRIDE not reused.
- [ ] server-side gates.
- [ ] RoleMenuPermission examples use IsAllowed.

## Import/export

- [ ] no new spreadsheet package.
- [ ] template version.
- [ ] stable ReviewRowKey.
- [ ] import changes NewPrice/Selected only.
- [ ] formula/key tampering rejected.
- [ ] no direct DB import.
- [ ] still Preview + Apply.

## Quality

- [ ] no large Razor inline @code.
- [ ] no inline CSS.
- [ ] no TextExpression runtime issue.
- [ ] light/dark.
- [ ] responsive.
- [ ] busy/disabled double-submit.
- [ ] actionable messages.
- [ ] no N+1.
- [ ] no unbounded data.

## Verification

- [ ] MenuDeploymentParityTests green.
- [ ] pricing tests green.
- [ ] inventory item master tests green.
- [ ] sales inquiry tests green.
- [ ] new unit/integration tests green.
- [ ] SQL Server concurrency tests actually executed.
- [ ] solution build 0 errors.
- [ ] full test run 0 failures.
- [ ] DDL script twice.
- [ ] menu script twice.
- [ ] manual scenarios A-F.

---

# 100. Final approval

## APPROVED FOR IMPLEMENTATION — 10 / 10

Approved against:

```text
mokth/net10projectTemplate
branch: production
commit: 6b729eb24be8f00e3b02ba200cb2e02e920d1e05
```

The AI coding agent is authorized to implement this plan in the stated phases.

The implementation is **not approved** if it:

- replaces the existing pricing engine;
- adds new runtime price hierarchy levels;
- permits partial bulk price updates;
- omits audit from existing price master write paths;
- treats price history as branch-scoped master data;
- silently creates Customer Item prices;
- creates overlapping future Price List windows;
- writes master price and audit in separate transactions;
- removes current price provenance / price-inquiry behavior;
- changes existing Sales Price History from POSTED invoice history into master-price history.

The approved final architecture is:

```text
                    EXISTING RUNTIME PRICING ENGINE
             (unchanged source eligibility and precedence)
                               |
             +-----------------+------------------+
             |                 |                  |
        SaItemCust        IvCustPrice       IvStockMaster
     Customer Special      Price List        Item Default
             |                 |                  |
             +-----------------+------------------+
                               |
                  Sales documents / Price Inquiry
                               |
                      persisted provenance
                               |
                POSTED Sales Price History
                  (actual selling result)


                 NEW PRICE REVIEW & UPDATE
                               |
             filters / calculate / exceptions
                    preview / reason
                               |
                       ATOMIC APPLY
                               |
             +-----------------+------------------+
             |                 |                  |
        SaItemCust        IvCustPrice       IvStockMaster
             |                 |                  |
             +-----------------+------------------+
                               |
                 SaPriceChangeBatch / Line
                    immutable company history
                               |
                  Price Change History Inquiry
                               |
              future sales / marketing analysis
```

This design is practical for an SME ERP because it keeps price maintenance fast and simple while preserving a deterministic pricing engine and collecting enough high-quality history for future analysis without introducing enterprise-level pricing complexity.
