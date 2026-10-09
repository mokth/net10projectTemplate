# Work Order — Select Delivery Request from Work Order Entry
## FINAL VERIFIED AI CODE AGENT IMPLEMENTATION PLAN

Repository: `mokth/net10projectTemplate`  
Branch: `productionv2`  
Verified current HEAD: `1b806666b9518bff6760239cf7b31b9d10530c81`  
Plan readiness: **10/10**  
Status: **APPROVED FOR IMPLEMENTATION**

---

# Review Amendments Applied

The previous plan was re-verified against the current `productionv2` source and amended for the following repository-confirmed issues:

1. The previous baseline `1a4d28bd...` is stale. Current HEAD is `1b806666...`.
2. The repository now contains `.agents/skills/devexpress-blazor-editor-binding-safety/skill.md`; all new DevExpress editor bindings MUST follow it.
3. `PreviewFromDeliveryRequestAsync(...)` currently calls fulfilment reconciliation but discards the returned `ProductionUnplannedQty`, then previews against `RequestedQty - active allocations`. This can make preview disagree with final create.
4. Saved DR-linked Draft quantity adjustment currently validates against `RequestedQty - other active allocations`, not reconciled fulfilment demand. This can allow a Draft allocation to grow beyond current production demand after delivery/shipment changes.
5. The previous plan invented a dedicated lookup query/page shape even though the repository already has the standard `LargeLookupSearchRequest` / `LargeLookupPage<T>` contract and standard `*SearchPopup` UI pattern.
6. The previous tests did not include a SQL Server race proving two planners cannot both consume the same remaining DR production quantity.

All six points are corrected below.

---

# Objective

Add a production-side **SELECT DELIVERY REQUEST** workflow to **New Work Order Entry**.

A production planner MUST be able to:

`Work Order New -> Select eligible DR -> review demand -> Preview (optional) -> Save Draft or Create + Release`

The implementation MUST preserve the existing authority and traceability:

`Sales Order -> Delivery Request -> PrWorkOrderDemandAllocation -> Work Order`

The existing Delivery Request page action:

`Delivery Request -> Create draft Work Order`

MUST remain supported and MUST continue using the same DR-aware Work Order service.

The feature is a second UI entry point, not a second business workflow.

---

# Confirmed Problems

## P1 — Work Order Entry cannot select a DR

Verified:

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

Current `SourceOptions` contains only:

`ProductionSourceTypes.Manual`

`PrWorkOrderEntry` has no production-side DR lookup.

## P2 — DR source type cannot be created through the normal WO API

Verified:

`ErpWeb.Core/Production/ProductionWorkOrderService.cs`

Normal Work Order draft validation rejects any source type other than Manual.

Therefore the implementation MUST NOT add `DELIVERY_REQUEST` to the current combo and then call:

`CreateDraftAsync(...)`

The DR-specific APIs are the required authority:

- `PreviewFromDeliveryRequestAsync(...)`
- `CreateDraftFromDeliveryRequestAsync(...)`
- `CreateAndReleaseFromDeliveryRequestAsync(...)`

## P3 — Existing DR search service has the wrong authorization boundary for Production

Verified:

`ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

`ISaDeliveryRequestService.SearchAsync(...)` authorizes against:

`MenuCodes.SalesDeliveryRequest`

A Work Order planner MUST NOT require Sales Delivery Request menu permission merely to select production demand.

The production-side lookup MUST authorize through:

`MenuCodes.PlanningWorkOrder`

with Work Order `Add` permission.

## P4 — DR preview currently uses the wrong post-reconcile quantity

Verified:

`ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

`PreviewFromDeliveryRequestAsync(...)` currently:

1. calls `_deliveryRequestFulfilment.ReconcileAsync(...)`;
2. ignores `reconcile.Data.ProductionUnplannedQty`;
3. calls `BuildDeliveryRequestSnapshotAsync(...)` without `reconciledUnplannedQty`;
4. the builder therefore falls back to `RequestedQty - active allocations`.

Final creation already uses reconciled `ProductionUnplannedQty`.

Preview and final creation can therefore disagree.

This MUST be fixed as part of this implementation because the new WO-side workflow exposes DR Preview directly.

## P5 — Saved DR Draft quantity adjustment is weaker than DR creation

Verified:

`ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

`AdjustDeliveryRequestAllocationAsync(...)` currently calculates:

`RequestedQty - other active allocations`

It does not use current fulfilment facts.

A DR's current production need can change because of delivered quantity or shipment coverage.

A saved Draft MUST NOT be allowed to grow beyond current reconciled production demand.

## P6 — Current repository has a standard large-lookup implementation pattern

Verified:

- `ErpWeb.Core/Lookups/LargeLookupContracts.cs`
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor`
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor.cs`
- `ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor`
- `ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor.cs`

The DR picker MUST follow this existing pattern rather than inventing a separate paging convention.

---

# Scope / Non-Goals

## In Scope

- production-authorized eligible DR lookup;
- server paging using the existing large-lookup contracts;
- standard DevExpress search popup;
- New Work Order -> Select DR;
- selected DR demand summary;
- optional DR preview;
- Save Draft from DR;
- Create + Release from DR;
- correct reconciled DR preview quantity;
- harden saved DR Draft allocation validation against current fulfilment demand;
- validate current DR allocation again on release;
- preserve DR allocation/audit/rollback behavior;
- focused unit tests;
- SQL Server concurrent-create race test.

## Non-Goals

DO NOT:

- redesign Delivery Request lifecycle;
- redesign Sales Order -> DR sourcing;
- allow one Work Order to consume multiple DRs;
- replace `PrWorkOrderDemandAllocation`;
- create a second DR/WO relationship;
- change costing;
- change Material Issue posting;
- change Finished Good Receipt costing/posting;
- change Inventory posting;
- change Procurement logic;
- change Accounting logic;
- change warehouse/project/priority DR rules;
- change Sales DR permissions;
- remove the DR-side `Create draft Work Order` action;
- add database columns/tables/indexes for this feature;
- add client-side quantity reservations merely because a picker row was selected.

---

# Exact Files

## Existing files to modify

1. `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
2. `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
3. `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
4. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
5. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`
6. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.css` — only for selected-DR presentation if required
7. `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
8. `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`

## Proposed new files

9. `ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor`
10. `ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor.cs`

No DI registration file change is required.

Verified registrations already exist in:

`ErpWeb.Core/CoreServiceCollectionExtensions.cs`

for:

- `IProductionWorkOrderService`
- `ISaDeliveryRequestService`
- `ISaDeliveryRequestFulfilmentService`

---

# Database Changes

**NONE.**

Keep the existing relational authority:

- `SaDeliveryRequest`
- `SaDeliveryRequestSource`
- `SaDeliveryRequestStockReservation`
- `PrWorkOrderDemandAllocation`
- `ProductionWorkOrder`

Keep the verified unique index:

`UQ_PrWorkOrderDemandAllocation_WorkOrder`

One Work Order MUST continue to have at most one DR allocation.

No migration.
No SQL alter script.
No backfill.
No new FK.
No new index.

---

# Exact Changes

## 1 — Reuse the repository large-lookup contract

File:

`ErpWeb.Core/Production/IProductionWorkOrderService.cs`

Add:

`using ErpWeb.Core.Lookups;`

### Proposed new DTO

Add:

`ProductionWorkOrderDeliveryRequestLookupRow`

Required fields:

- `long DeliveryRequestId`
- `string DeliveryRequestNo`
- `string ProductCode`
- `string? ProductDescription`
- `string ProductionUom`
- `decimal RequestedQty`
- `decimal WoAllocatedQty`
- `decimal ProductionUnplannedQty`
- `DateTime RequiredDate`
- `string? DefinitionCode`
- `string? WarehouseCode`
- `string? ProjectCode`
- `string? Priority`
- `string? Remark`
- `string? PrimarySoNo`
- `int ActiveSoCount`
- `string? PrimaryCustomerCode`
- `int ActiveCustomerCount`
- `byte[] RowVersion`

Do NOT expose only one Customer as though a DR is guaranteed to contain one customer.

The DR source model permits multiple source rows; the lookup DTO MUST make multiple-source display explicit.

### Proposed new service method

Add to `IProductionWorkOrderService`:

```csharp
Task<LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>>
    SearchEligibleDeliveryRequestsAsync(
        LargeLookupSearchRequest request,
        CancellationToken cancellationToken = default);
```

Do NOT invent a second custom pagination request/page type.

---

## 2 — Implement production-authorized DR lookup

File:

`ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

Add:

`using ErpWeb.Core.Lookups;`

Implement:

`SearchEligibleDeliveryRequestsAsync(...)`

### Authorization

MUST call the existing Production Work Order authorization boundary:

`AuthorizeAsync(PermissionCodes.Add, requireWriteScope: false, cancellationToken)`

Reason:

- picker is read-only;
- branch scope is sufficient for lookup;
- final Preview/Create still requires write scope/location through existing methods.

MUST NOT call:

`ISaDeliveryRequestService.SearchAsync(...)`

because that enforces the Sales DR menu.

### Header candidate filter

Filter `SaDeliveryRequests` by:

- current `CompanyCode`;
- current `BranchCode`;
- `Status == SaDeliveryRequestStatuses.Released`
  OR
  `Status == SaDeliveryRequestStatuses.InProduction`.

### Search text

When `LargeLookupSearchRequest.SearchText` is nonblank, match:

- `DeliveryRequestNo`;
- `ProductCode`;
- `ProductDescription`;
- active `SaDeliveryRequestSource.SoNo`;
- active `SaDeliveryRequestSource.CustomerCode`.

Only active source rows may participate in SO/customer matching.

### Sort

MUST use deterministic ordering:

1. `RequiredDate` ascending;
2. `DeliveryRequestNo` ascending;
3. `Uid` ascending.

### Eligibility authority

MUST use:

`_deliveryRequestFulfilment.GetFactsBatchAsync(...)`

Do not calculate eligibility as only:

`RequestedQty - WorkOrderAllocationQty`

Eligible means:

`facts.ProductionUnplannedQty > 0.0001m`

If `_deliveryRequestFulfilment` is null:

FAIL CLOSED with a lookup error.

If facts retrieval fails:

FAIL CLOSED.

If a candidate DR is missing from returned facts:

FAIL CLOSED rather than silently treating it as eligible.

### Read-only requirement

The lookup MUST use:

`GetFactsBatchAsync(...)`

It MUST NOT call:

`ReconcileAsync(...)`

A lookup must not mutate DR stock reservations merely because the picker opened.

### Paging algorithm

Eligibility depends on computed fulfilment facts, therefore SQL header paging cannot be treated as final eligibility paging.

Implement a bounded candidate scan:

1. normalize request using `LargeLookupSearchRequest.NormalizedSkip` and `NormalizedTake`;
2. create the ordered candidate query;
3. scan candidate headers in bounded chunks, recommended `200` or `400`;
4. call `GetFactsBatchAsync(...)` once per chunk;
5. apply `ProductionUnplannedQty > 0.0001m`;
6. maintain the eligible-row index;
7. collect only rows falling within the requested eligible `Skip/Take`;
8. continue scanning to obtain the exact eligible `TotalCount`;
9. query active DR sources only for the final selected page IDs to build source/customer summaries.

MUST NOT materialize complete source graphs for every historical DR.

### Source summary

For page rows only:

- query active `SaDeliveryRequestSource` rows;
- distinct/order SO numbers;
- distinct/order nonblank customer codes;
- populate:
  - `PrimarySoNo`
  - `ActiveSoCount`
  - `PrimaryCustomerCode`
  - `ActiveCustomerCount`

UI can display:

- one SO normally;
- `SO001 +2` when more exist;
- one customer normally;
- `CUST01 +1` when more exist.

Do not imply all DR demand belongs to the first customer.

---

## 3 — Fix DR Preview to use reconciled production demand

File:

`ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

Method:

`PreviewFromDeliveryRequestAsync(...)`

Current defect:

reconcile result is discarded.

### Required fix

Use:

`reconcile.Data.ProductionUnplannedQty`

as the `reconciledUnplannedQty` passed to:

`BuildDeliveryRequestSnapshotAsync(...)`.

Required shape:

1. authorize;
2. create DB context;
3. if fulfilment service exists:
   - call `ReconcileAsync(DeliveryRequestId)`;
   - require success and non-null Data;
   - capture `ProductionUnplannedQty`;
4. call `BuildDeliveryRequestSnapshotAsync(...)` with that captured quantity;
5. map preview.

The preview's:

`DeliveryRequestUnplannedQty`

MUST reflect the reconciled fulfilment result.

### Important side-effect rule

`PreviewFromDeliveryRequestAsync(...)` currently calls `ReconcileAsync(...)`.

That reconciliation can write/update DR soft stock reservation rows.

Therefore:

**DO NOT automatically call Preview when a user merely selects a DR in the popup.**

Preview remains an explicit user action:

`CALCULATE PREVIEW`

Final Create still re-locks and revalidates everything transactionally.

### Message correction

`BuildDeliveryRequestSnapshotAsync(...)` currently allows:

- Released;
- In Production;

but its error text says only Released.

Correct the message to state:

`Only a Released or In Production Delivery Request can create a Work Order.`

Do not change the allowed statuses.

---

## 4 — Harden saved DR Draft quantity adjustment

File:

`ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

Method:

`AdjustDeliveryRequestAllocationAsync(...)`

Current validation:

`deliveryRequest.RequestedQty - otherActive`

is insufficient once fulfilment has changed.

### Required authority

For a DR-linked Draft WO with an existing active allocation:

1. lock DR using existing lock order;
2. lock DR allocations using existing lock order;
3. when `_deliveryRequestFulfilment` is available, call:

`ReconcileInTransactionAsync(...)`

4. derive current allowable quantity for this Work Order from reconciled facts.

For a Draft Work Order there must be no downstream production execution because existing reopen/edit rules already guard execution.

Use:

```text
otherOutstandingSupply =
    max(facts.OutstandingWoSupplyQty - currentAllocation.AllocatedQty, 0)

maxAllowedForThisWorkOrder =
    max(facts.ProductionRequiredQty - otherOutstandingSupply, 0)
```

Round using existing `IvQty` rules.

Reject when:

`plannedQty > maxAllowedForThisWorkOrder + 0.0001m`

Error MUST clearly state the currently allowed quantity.

### Compatibility fallback

If `_deliveryRequestFulfilment` is null in a direct/unit-test construction path, retain the existing fallback:

`RequestedQty - other active allocation`

Do not change production DI; normal application DI already supplies the fulfilment service.

### Allocation update

On success:

- update only the existing allocation;
- keep `IsActive`;
- write existing `AllocationChanged` audit;
- return/propagate the DR ID so the caller knows fulfilment needs post-persist reconciliation.

Do NOT create a second allocation row.

---

## 5 — Reconcile fulfilment after a saved DR Draft quantity changes

File:

`ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`

Current helper:

`DraftHeaderMutationResult(bool Changed)`

Extend the private mutation result so it can carry:

`long? DeliveryRequestIdToReconcile`

or equivalent.

### `ApplyDraftHeaderChangesAsync(...)`

When DR planned quantity changes:

- call the hardened `AdjustDeliveryRequestAllocationAsync(...)`;
- capture the related DR ID;
- return it in the mutation result.

### `UpdateDraftHeaderAsync(...)`

If a DR allocation quantity changed and fulfilment service is available:

1. persist Work Order + allocation changes with `SaveChangesAsync` inside the existing transaction;
2. call `ReconcileInTransactionAsync(...)` for that DR so the fulfilment query sees the newly persisted allocation inside the same transaction;
3. `SaveChangesAsync` again for updated soft reservations;
4. commit.

If any step fails:

ROLL BACK the existing transaction.

For non-DR or no quantity change:

retain the existing one-save path.

### `UpdateAndReleaseAsync(...)`

If a DR allocation quantity changed:

1. apply header/allocation mutation;
2. `SaveChangesAsync` inside the current transaction;
3. reconcile the DR with the new allocation;
4. continue existing Release validation;
5. perform DR release-allocation logic;
6. final `SaveChangesAsync`;
7. commit.

Any failure MUST roll back all intermediate saves because they remain inside the same transaction.

DO NOT create a new transaction.

---

## 6 — Validate DR allocation against current demand at Release

File:

`ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

Method:

`MarkDeliveryRequestWorkOrderReleasedAsync(...)`

Before marking the allocation released / reconciling DR status:

1. obtain the required DR + allocation using the existing relational checks;
2. if fulfilment service is available:
   - call `ReconcileInTransactionAsync(...)`;
   - calculate `otherOutstandingSupply`;
   - calculate `maxAllowedForThisWorkOrder` using the same rule as Draft resize;
   - FAIL CLOSED if the existing allocation now exceeds current reconciled production demand;
3. then continue existing allocation release metadata/audit/status reconciliation.

This covers:

- Release saved Draft;
- Update + Release;
- Create + Release from DR.

If demand has fallen below the WO allocation, instruct user to reduce/re-plan the Draft before release.

This validation MUST remain inside the existing Work Order transaction.

---

## 7 — Preserve DR SourceReference as relational provenance

File:

`ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`

Method:

`ApplyDraftHeaderChangesAsync(...)`

For:

`entity.SourceType == ProductionSourceTypes.DeliveryRequest`

MUST NOT replace `entity.SourceReference` from a caller-supplied editable string.

Keep the persisted DR reference unchanged.

For Manual Work Orders:

retain existing editable `SourceReference` behavior.

The relational authority remains:

`PrWorkOrderDemandAllocation`

but the display/provenance reference must not drift away from its DR.

---

## 8 — Add standard Planning DR search popup

### Proposed new file

`ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor`

Mirror the repository pattern from:

`ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor`

Use:

- `DxPopup`;
- `CssClass="common-popup"`;
- `CloseOnOutsideClick="false"`;
- `DxTextBox` search;
- Search button;
- `DxGrid`;
- focused-row selection;
- double-click select;
- PREV / NEXT;
- SELECT;
- CANCEL;
- Enter key search/select;
- Escape close.

Recommended width:

`90vw` or an equivalent responsive width consistent with the common popup.

### Columns

Show:

- DR No
- Product
- Description
- Required Date
- Priority
- Requested Qty
- WO Allocated Qty
- Available to Plan
- UOM
- Warehouse
- Project
- SO summary
- Customer summary

Do not show decorative columns that are not useful for production selection.

### Proposed code-behind

`ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor.cs`

Mirror:

`IvStockMasterSearchPopup.razor.cs`

Use:

- `LargeLookupSearchRequest.DefaultPageSize`;
- cancellation token source per load;
- `_loadSequence`;
- `_loading`;
- `_skip`;
- `_totalCount`;
- focused row;
- explicit server search;
- Prev/Next.

Inject only:

`IProductionWorkOrderService`

Call:

`SearchEligibleDeliveryRequestsAsync(...)`.

Parameters:

- `bool Visible`
- `EventCallback<bool> VisibleChanged`
- `EventCallback<ProductionWorkOrderDeliveryRequestLookupRow> Selected`
- optional `InitialSearchText`

Do not inject `ISaDeliveryRequestService`.

---

## 9 — Mandatory DevExpress binding safety

Before editing UI, the Code Agent MUST read and follow:

`.agents/skills/devexpress-blazor-editor-binding-safety/skill.md`

### Required rules

For new interactive editors:

Prefer normal `@bind-*`.

Examples:

- `DxTextBox` -> `@bind-Text`
- `DxSpinEdit` -> `@bind-Value`
- `DxDateEdit` -> `@bind-Date`
- `DxComboBox` -> `@bind-Value`

If an explicit Changed callback is required, provide the matching expression:

- `TextExpression`
- `ValueExpression`
- `DateExpression`
- etc.

The expression MUST reference the same bound property.

MUST NOT use:

`ValidationEnabled="false"`

as a workaround for a missing expression.

It is allowed only for genuine display-only/read-only editors.

The new popup search box SHOULD use:

`@bind-Text`

exactly like the existing stock/customer search popups.

---

## 10 — Work Order Entry transient DR state

File:

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

Add transient state:

- `bool DeliveryRequestPickerVisible`
- `ProductionWorkOrderDeliveryRequestLookupRow? SelectedDeliveryRequest`

Add:

`IsNewDeliveryRequestMode`

true only when:

- `IsNewMode`;
- `SelectedDeliveryRequest != null`.

Do not infer selected DR solely from free-text `SourceReference`.

### Derived editability

Change:

`CanEditProduct`

to require:

`!IsNewDeliveryRequestMode`

Change:

`CanEditDefinition`

to require:

`!IsNewDeliveryRequestMode`

The selected DR owns:

- product;
- production UOM;
- definition code;
- DR identity.

### Preview menu visibility

Current More -> `CALCULATE PREVIEW` is hidden whenever `IsDeliveryRequestSource`.

Change the condition so:

- Manual New -> preview visible;
- Selected DR New -> preview visible;
- Saved DR Work Order -> keep current preview unavailable behavior.

Use a derived property rather than duplicating complex Razor expressions.

---

## 11 — Select DR behavior

File:

`PrWorkOrderEntry.razor.cs`

Add:

`OpenDeliveryRequestPicker()`

or async equivalent.

Only allow when:

- New mode;
- `CanAdd`;
- not submitting.

### On selected DR

Add:

`OnDeliveryRequestSelectedAsync(ProductionWorkOrderDeliveryRequestLookupRow row)`

Set:

- `SelectedDeliveryRequest = row`
- `Request.SourceType = ProductionSourceTypes.DeliveryRequest`
- `Request.SourceReference = row.DeliveryRequestNo`
- `Request.ProductCode = row.ProductCode`
- `Request.DefinitionCode = row.DefinitionCode ?? PrProductDefinitionCodes.Standard`
- `Request.PlannedQty = row.ProductionUnplannedQty`
- `Request.PlannedStartDate = row.RequiredDate.Date`
- `Request.PlannedCompletionDate = row.RequiredDate.Date`
- `Request.SchedulingDirection = ProductionSchedulingDirections.Forward`
- `Request.Remark = row.Remark`
- `_selectedProductDescription = row.ProductDescription`
- `_selectedProductUom = row.ProductionUom`
- `PreviewModel = null`
- `_previewInputFingerprint = null`

Then load active definition options for display only:

`LoadDefinitionOptionsAsync(row.ProductCode, selectDefaultWhenEmpty: false)`

MUST NOT change the DR-selected definition because of the definition list load.

MUST NOT call DR Preview automatically.

MUST NOT create `PrWorkOrderDemandAllocation` on selection.

---

## 12 — Clear / Change DR behavior

File:

`PrWorkOrderEntry.razor.cs`

Add:

`ClearSelectedDeliveryRequest()`

New-mode only.

It MUST clear all DR-derived transient state:

- `SelectedDeliveryRequest`
- product code
- selected product description
- selected UOM
- definition options
- DR source reference
- DR preview
- preview fingerprint

Reset to a valid Manual New Work Order:

- `SourceType = Manual`
- `DefinitionCode = Standard`
- `PlannedQty = 1`
- Start/Completion = today
- Forward scheduling
- `Remark = null`

Do not preserve the DR remark when clearing the DR.

`CHANGE DR` simply reopens the picker; selecting another row replaces the unsaved DR-derived values.

No saved Work Order may be detached from a DR using this UI.

---

## 13 — Build one DR request mapping

File:

`PrWorkOrderEntry.razor.cs`

Add one helper:

`BuildDeliveryRequestWorkOrderRequest()`

It MUST require `SelectedDeliveryRequest != null`.

Map:

- `DeliveryRequestId`
- `PlannedQty`
- `DefinitionCode`
- `PlannedStartDate`
- `PlannedCompletionDate`
- `SchedulingDirection`
- `Remark`
- `DeliveryRequestRowVersion`

from:

- current Work Order input state;
- selected DR ID;
- selected DR RowVersion.

Do not duplicate this mapping separately in Preview/Save/Release.

---

## 14 — Preview branching

File:

`PrWorkOrderEntry.razor.cs`

Method:

`ProcessPreviewAsync()`

Required branch order:

1. If `IsNewDeliveryRequestMode`:
   - call `PreviewFromDeliveryRequestAsync(BuildDeliveryRequestWorkOrderRequest())`;
   - use existing failure handling;
   - on success assign `PreviewModel`;
   - update preview fingerprint;
   - display DR-specific preview status;
   - do not create allocation.
2. Else if saved `IsDeliveryRequestSource`:
   - retain current message that saved DR WOs already use their demand snapshot.
3. Else:
   - retain normal Manual `ProcessPreviewAsync(Request)`.

For selected DR summary, current available quantity may display:

`PreviewModel?.DeliveryRequestUnplannedQty ?? SelectedDeliveryRequest.ProductionUnplannedQty`

so an explicit preview can show the reconciled quantity.

---

## 15 — Save Draft branching

File:

`PrWorkOrderEntry.razor.cs`

Method:

`SaveDraftAsync()`

Required New-mode branch:

```text
New + selected DR
    -> CreateDraftFromDeliveryRequestAsync(...)

New + no selected DR
    -> existing CreateDraftAsync(Request)

Existing Draft
    -> existing UpdateDraftHeaderAsync(...)
```

After successful DR creation:

- use returned `ProductionWorkOrderDetail`;
- call existing `ApplyDetail(...)`;
- clear transient selected-DR picker state;
- navigate using existing `/planning/work-orders/edit/{WorkOrderNo}` behavior.

MUST NOT manually create an allocation in the UI.

---

## 16 — Create + Release branching

File:

`PrWorkOrderEntry.razor.cs`

Method:

`CreateAndReleaseAsync()`

Required:

```text
New + selected DR
    -> CreateAndReleaseFromDeliveryRequestAsync(...)

New + no selected DR
    -> existing CreateAndReleaseAsync(Request)
```

Do NOT implement:

`Create Draft -> separate Release API`

for new DR creation.

The existing atomic DR create-and-release service MUST remain the authority.

Update Release confirmation text in New DR mode so the user can see:

- DR number;
- planned quantity;
- that the Work Order will allocate DR production demand.

---

## 17 — Work Order source UI

File:

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`

The current one-option Source Type combo is not meaningful.

### New Manual mode

Display:

`Demand source: Manual Work Order`

and button:

`SELECT DELIVERY REQUEST`

### New selected-DR mode

Display:

`Demand source: Delivery Request`

plus:

- DR number;
- Product;
- UOM;
- Required date;
- Requested;
- WO allocated;
- Available to plan;
- Priority;
- Warehouse;
- Project;
- SO summary;
- Customer summary.

Actions:

- `CHANGE DR`
- `CLEAR DR`

### Saved Work Order

Display Source Type as read-only presentation.

Do not present an editable one-option combo.

Manual `SourceReference` remains editable where current Manual Draft behavior permits it.

DR `SourceReference` remains read-only.

### Product and Definition

When DR is selected:

- Product is locked from DR;
- Product Definition is locked from DR/default definition;
- UOM is read-only;
- Planned Qty remains editable;
- schedule remains editable;
- Remark remains editable.

Add a clear hint:

`Product, UOM and definition come from the selected Delivery Request.`

Do not show an enabled lookup/search control for a field the DR owns.

---

# Transaction / Execution Order

## A — DR lookup

1. authorize Planning Work Order Add using branch scope;
2. query Released/In-Production DR headers in company/branch;
3. apply search text;
4. deterministic sort;
5. scan headers in bounded batches;
6. read fulfilment facts using `GetFactsBatchAsync`;
7. filter `ProductionUnplannedQty > 0.0001`;
8. collect requested eligible page;
9. continue scan for exact eligible count;
10. load active source summaries for page IDs;
11. return `LargeLookupPage`;
12. write nothing.

## B — Explicit DR Preview

Preserve existing Preview behavior except for the quantity bug:

1. authorize Work Order Add with write scope;
2. reconcile DR fulfilment;
3. use returned `ProductionUnplannedQty`;
4. build Work Order snapshot preview;
5. validate DR Product Definition/UOM;
6. return preview;
7. create no Work Order/allocation.

The fulfilment reconciliation may update soft stock reservations. This is why preview MUST remain explicit.

## C — Create Draft from DR

Preserve existing service transaction:

1. authorize Add;
2. begin transaction;
3. lock DR;
4. validate DR RowVersion when supplied;
5. validate Released/In-Production;
6. lock DR allocations;
7. reconcile fulfilment in transaction;
8. obtain authoritative `ProductionUnplannedQty`;
9. reject planned quantity above current unplanned demand;
10. build Product Definition snapshot;
11. validate Output UOM == DR Production UOM;
12. allocate Work Order number;
13. create Work Order;
14. create exactly one `PrWorkOrderDemandAllocation`;
15. write Work Order/DR audits;
16. save;
17. commit.

## D — Resize saved DR Draft

1. lock DR bridge before Work Order using existing lock order;
2. lock/load Draft Work Order;
3. lock DR + allocations through existing helper path;
4. reconcile fulfilment;
5. derive `maxAllowedForThisWorkOrder`;
6. reject excessive planned quantity;
7. update existing allocation;
8. recalculate Work Order quantities/schedule/hash;
9. save Work Order + allocation inside current transaction;
10. reconcile fulfilment again against the newly persisted allocation;
11. save reservation changes;
12. commit.

Any failure rolls back the transaction.

## E — Release DR Work Order

Before final release commit:

1. obtain required DR allocation;
2. reconcile fulfilment;
3. validate current allocation <= current allowable production demand;
4. continue existing Release validation;
5. set allocation release metadata/audit;
6. reconcile DR lifecycle status;
7. save;
8. commit.

FAIL CLOSED when current DR demand can no longer support the WO quantity.

---

# Invariants

1. One Work Order MUST have at most one `PrWorkOrderDemandAllocation`.
2. One DR MAY have multiple Work Orders.
3. Picker selection MUST NOT create/reserve a WO allocation.
4. New DR WO quantity MUST NOT exceed transaction-time reconciled `ProductionUnplannedQty`.
5. Saved DR Draft quantity MUST NOT exceed current reconciled production capacity for that Work Order.
6. A DR Work Order MUST NOT release if current reconciled DR demand no longer supports its allocation.
7. Draft/Cancelled/Completed DRs MUST NOT be selectable.
8. DR with `ProductionUnplannedQty <= 0.0001` MUST NOT be selectable.
9. Normal `CreateDraftAsync` MUST continue rejecting `DELIVERY_REQUEST`.
10. DR relational authority MUST remain `PrWorkOrderDemandAllocation`.
11. `SourceReference` MUST NOT be used as relational authority.
12. DR `SourceReference` MUST not drift from its linked DR.
13. Manual Work Order behavior MUST remain unchanged.
14. DR-side `Create draft Work Order` MUST remain unchanged.
15. All intermediate saves for DR Draft resize/release MUST remain inside the existing transaction and roll back atomically on failure.

---

# Rollback / Reversal

No new reversal model is introduced.

Existing behavior MUST remain:

- unsaved selected DR -> Clear only resets UI state;
- Cancel Draft -> existing `DeactivateDeliveryRequestAllocationAsync(...)`;
- safe never-released Delete Draft -> existing allocation delete/audit behavior;
- Reopen -> existing DR lifecycle reconciliation;
- released/executed WOs remain protected by existing blockers.

The new lookup/preview path MUST NOT weaken any rollback guard.

Intermediate `SaveChangesAsync` required for DR resize reconciliation MUST remain inside the already-open EF transaction, so rollback restores all changes.

---

# Concurrency / Locking

Preserve the existing SQL Server lock order in:

`ProductionWorkOrderService.DeliveryRequest.cs`

The final Create/Release service, not the picker, is authoritative.

The picker RowVersion is an early concurrency token only.

Two planners may select the same displayed DR quantity.

The server MUST guarantee that concurrent creation cannot both consume the same remaining quantity.

Do not introduce a client-side "reservation on selection".

Do not introduce a new locking subsystem.

Do not remove:

- DR `UPDLOCK/ROWLOCK/HOLDLOCK`;
- DR allocation locking;
- existing transaction boundaries.

---

# Tests

## A — `ProductionWorkOrderServiceTests.cs`

Update the private `CreateSut(...)` helper to optionally accept:

`ISaDeliveryRequestFulfilmentService? deliveryRequestFulfilment`

and pass it to the verified optional constructor parameter.

Do not replace existing tests.

### Lookup tests

Add:

1. `Eligible_DR_lookup_returns_only_released_or_inproduction_with_positive_unplanned_qty`
   - Released positive -> included
   - InProduction positive -> included
   - Draft -> excluded
   - Cancelled -> excluded
   - Completed -> excluded
   - zero unplanned -> excluded

2. `Eligible_DR_lookup_uses_planning_work_order_add_permission`
   - deny `PermissionCodes.Add`
   - result fails
   - no Sales DR permission dependency is introduced

3. `Eligible_DR_lookup_is_company_branch_scoped`
   - current branch row included
   - other branch row excluded

4. `Eligible_DR_lookup_searches_dr_product_so_and_customer`
   - DR no
   - product
   - active SO
   - active customer

5. `Eligible_DR_lookup_pages_after_eligibility_filter`
   - noneligible candidates before eligible rows do not consume page slots
   - `TotalCount` is eligible count

6. `Eligible_DR_lookup_returns_creation_metadata`
   Assert:
   - definition
   - warehouse
   - project
   - priority
   - required date
   - remark
   - RowVersion
   - requested
   - allocated
   - unplanned
   - source/customer counts

7. `Eligible_DR_lookup_is_read_only`
   Assert lookup creates no:
   - Work Order
   - `PrWorkOrderDemandAllocation`
   - DR audit event

### Preview consistency tests

8. `Delivery_request_preview_uses_reconciled_production_unplanned_qty`
   - mock `ReconcileAsync` with lower `ProductionUnplannedQty` than `RequestedQty - allocations`
   - preview above reconciled quantity MUST fail

9. `Delivery_request_preview_reports_reconciled_unplanned_qty`
   - valid preview returns the same reconciled value in `DeliveryRequestUnplannedQty`

### Creation regression tests

Keep existing:

`Delivery_request_creation_uses_snapshot_pipeline_and_persists_one_demand_allocation`

Add/strengthen:

10. stale DR RowVersion -> concurrency, no WO/allocation

11. planned quantity > transaction-time reconciled unplanned -> fails, no WO/allocation

12. multiple WOs from same DR remain allowed until current production demand is consumed

13. normal Manual `CreateDraftAsync` still rejects `DELIVERY_REQUEST`

### Saved Draft demand-integrity tests

14. `Delivery_request_draft_resize_rejects_quantity_above_reconciled_current_demand`

15. `Delivery_request_release_rejects_allocation_above_reconciled_current_demand`

16. `Delivery_request_source_reference_cannot_be_changed_by_draft_header_update`

Existing cancel/reopen/delete DR tests MUST remain green.

---

## B — SQL Server concurrency test

File:

`ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`

Add:

`using ErpWeb.Model.Entities.Sales;`

Add a valid Released DR seed helper to the existing SQL Server `Host`.

The DR MUST use:

- current test Company;
- current test Branch;
- `Host.ProductCode`;
- UOM `PCS`;
- current active Product Definition;
- positive requested quantity;
- at least one active DR source row;
- status Released.

### Required race test

Add:

`Two_concurrent_DR_work_order_creates_cannot_overallocate_same_delivery_request`

Setup:

- one DR;
- requested/unplanned quantity = `10`;
- capture same DR RowVersion;
- two independent `ProductionWorkOrderService` instances;
- both requests attempt `PlannedQty = 10`;
- start simultaneously with a gate.

Assert:

- exactly one succeeds;
- exactly one fails;
- only one Work Order exists for that DR allocation race;
- exactly one active `PrWorkOrderDemandAllocation` exists;
- allocated total = `10`, never `20`;
- loser failure is validation/concurrency, not partial success;
- transaction leaves no orphan Work Order or audit/allocation from loser.

This test is mandatory because SQLite cannot prove the SQL Server locking behavior.

---

# UI Verification

No bUnit-style Work Order UI test fixture is verified in this repository.

Therefore require build + manual smoke verification in addition to service tests.

Manual verification:

1. New Work Order defaults to Manual.
2. `SELECT DELIVERY REQUEST` opens standard popup.
3. Search Enter key works.
4. Search button works.
5. Prev/Next work.
6. focused row + SELECT works.
7. double-click works.
8. Escape/Cancel closes.
9. selecting DR does not auto-preview.
10. Product/Definition/UOM become locked.
11. Planned Qty remains editable.
12. Clear DR restores clean Manual New state.
13. Change DR replaces unsaved DR data.
14. Calculate Preview uses DR preview.
15. Save Draft creates relational DR allocation.
16. Create + Release is atomic.
17. Saved WO shows existing DR/SO trace section.
18. no DevExpress TextExpression/ValueExpression runtime error occurs.

---

# Implementation Order

STEP 1 — Read `.agents/skills/devexpress-blazor-editor-binding-safety/skill.md`.

STEP 2 — Add failing unit tests for:
- eligible DR lookup;
- preview reconciled quantity;
- DR resize demand validation;
- DR release demand validation;
- source-reference immutability.

STEP 3 — Add `ProductionWorkOrderDeliveryRequestLookupRow` and the `LargeLookupPage` lookup method contract.

STEP 4 — Implement production-authorized eligible DR lookup.

STEP 5 — Fix `PreviewFromDeliveryRequestAsync(...)` to use reconciled `ProductionUnplannedQty`.

STEP 6 — Harden `AdjustDeliveryRequestAllocationAsync(...)`.

STEP 7 — Update `DraftCommands` transaction flow for post-allocation fulfilment reconciliation.

STEP 8 — Harden DR Work Order Release validation.

STEP 9 — Add `PrDeliveryRequestSearchPopup.razor` and `.razor.cs` following the existing lookup popup standard.

STEP 10 — Add transient DR-selection state and request mapping in `PrWorkOrderEntry.razor.cs`.

STEP 11 — Branch Preview / Save Draft / Create + Release correctly for selected DR mode.

STEP 12 — Update Work Order Razor source/demand UI and minimal isolated CSS.

STEP 13 — Add SQL Server concurrent DR-create test.

STEP 14 — Run focused Work Order tests.

STEP 15 — Run Sales Delivery Request tests.

STEP 16 — Run SQL Server suite when configured.

STEP 17 — Build and run full regression suite.

---

# Verification Commands

At repository root:

```text
dotnet build ErpWeb.slnx
```

Focused Work Order tests:

```text
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~ProductionWorkOrderServiceTests"
```

Sales DR regression:

```text
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaDeliveryRequestServiceTests"
```

SQL Server Work Order concurrency suite when the scratch SQL Server test DB is configured:

```text
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~ProductionWorkOrderSqlServerConcurrencyTests"
```

Then:

```text
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

Do not claim completion if the build or required focused tests fail.

---

# Regression Areas

MUST remain unchanged unless explicitly hardened above:

- Manual Work Order creation;
- Product Definition snapshot creation;
- schedule calculation;
- Work Order numbering;
- DR-page Work Order creation;
- DR status reconciliation;
- DR fulfilment facts;
- DR stock reservation logic;
- Draft cancellation;
- Draft deletion;
- Reopen;
- Material Issue;
- Daily Production;
- Finished Good Receipt;
- production costing;
- Inventory valuation/posting;
- procurement trace;
- Sales Order -> DR trace;
- tenant/company/branch isolation;
- Work Order permissions;
- Sales DR permissions.

No costing path is changed by this plan.

---

# Do-Not Rules

DO NOT:

- add Delivery Request as a free editable Source Type option;
- call normal `CreateDraftAsync` for a DR Work Order;
- use `SourceReference` as the DR relationship;
- create an allocation from Razor/UI code;
- reserve DR demand when a picker row is merely selected;
- call `ReconcileAsync` just to populate the lookup grid;
- auto-preview after DR selection;
- duplicate fulfilment calculations in UI;
- duplicate `SaDeliveryRequestFulfilmentService` logic;
- bypass DR status validation;
- bypass DR RowVersion validation;
- bypass transaction-time production-demand validation;
- weaken existing SQL lock order;
- require Sales DR menu permission for Production DR selection;
- introduce a new pagination framework;
- load all DR history into the browser;
- use `ValidationEnabled=false` to hide DevExpress binding errors;
- use `Text + TextChanged` without `TextExpression`;
- use `Value + ValueChanged` without `ValueExpression`;
- change database schema;
- change costing/posting logic;
- refactor unrelated modules.

---

# Acceptance Criteria

- [ ] Plan is implemented against `productionv2` current source, not the obsolete `1a4d28bd...` baseline.
- [ ] New Work Order shows `SELECT DELIVERY REQUEST`.
- [ ] Picker uses Planning Work Order Add permission.
- [ ] Production user does not need Sales DR menu access for picker lookup.
- [ ] Picker uses `LargeLookupSearchRequest` / `LargeLookupPage<T>`.
- [ ] Picker follows existing DevExpress search-popup interaction pattern.
- [ ] Picker is server paged.
- [ ] Picker shows only Released/In-Production DRs with positive read-only fulfilment `ProductionUnplannedQty`.
- [ ] Lookup performs no DR/WO write.
- [ ] Search supports DR/product/SO/customer.
- [ ] Source/customer summaries do not falsely imply one source/customer.
- [ ] Selecting a DR performs no allocation and no automatic Preview.
- [ ] Product/UOM/Definition are locked from selected DR.
- [ ] Planned Qty/schedule/remark remain meaningful editable inputs.
- [ ] DR Preview uses reconciled `ProductionUnplannedQty`.
- [ ] DR Preview cannot report a quantity greater than reconciled demand.
- [ ] Save Draft uses `CreateDraftFromDeliveryRequestAsync`.
- [ ] Create + Release uses `CreateAndReleaseFromDeliveryRequestAsync`.
- [ ] Exactly one `PrWorkOrderDemandAllocation` is created per DR-linked WO.
- [ ] Saved DR Draft cannot be resized above current reconciled production demand.
- [ ] DR Work Order cannot Release when its allocation exceeds current reconciled demand.
- [ ] DR `SourceReference` cannot drift through Draft header update.
- [ ] Clear DR restores a clean Manual New Work Order.
- [ ] Existing Manual WO flow remains unchanged.
- [ ] Existing DR-page Create Work Order remains unchanged.
- [ ] Existing cancel/delete/reopen behavior remains green.
- [ ] SQL Server race proves two planners cannot double-consume one DR quantity.
- [ ] No database migration exists for this feature.
- [ ] No costing/inventory posting logic is changed.
- [ ] No new DevExpress editor binding runtime exception is introduced.
- [ ] `dotnet build ErpWeb.slnx` succeeds.
- [ ] Focused Work Order tests pass.
- [ ] Sales DR regression tests pass.
- [ ] SQL Server concurrency suite passes when configured/required.
- [ ] Full regression test suite passes.

---

# Review Scorecard

| Category | Score |
|---|---:|
| Repository correctness | 10/10 |
| Architecture compatibility | 10/10 |
| Data/schema correctness | 10/10 |
| Transaction integrity | 10/10 |
| Rollback/reversal completeness | 10/10 |
| Concurrency/locking safety | 10/10 |
| Regression safety | 10/10 |
| Test completeness | 10/10 |
| UI/repository convention alignment | 10/10 |
| Code-Agent implementability | 10/10 |
| Costing integrity | N/A — costing is explicitly unchanged |

**Overall implementation-plan readiness: 10/10**

# Approval Status

**APPROVED FOR IMPLEMENTATION**
