# Deep Study — ERP Transaction UI Standardization

## Scope

This study compared the current transaction UI patterns in the `production` branch across:

- Inventory
- Sales
- Purchase / Procurement
- Planning / Production execution
- shared DevExpress grid infrastructure
- global and isolated CSS ownership

The goal is not to redesign the application. It is to make AI-generated UI converge on the UI that already exists in the repository.

---

## 1. Main finding

The current skill is conceptually correct about consistency, but its central assumption is now too broad:

> Inventory is treated as the universal transaction entry reference.

That is no longer what the live repository does.

The live repository has one highly consistent **list-page standard**, but multiple legitimate **entry-page standards**.

The correct standardization model is therefore:

```text
Shared ERP UI contract
        |
        +-- Shared transaction list pattern
        |
        +-- Inventory batch entry pattern
        |
        +-- Sales commercial document pattern
        |
        +-- Purchase commercial document pattern
        |
        +-- Production workflow/workspace pattern
```

---

## 2. Evidence from the current repository

### 2.1 Shared list pages are genuinely common

Representative list pages:

- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor`
- `ErpWeb.UI/Sales/Transactions/SaSoList.razor`
- `ErpWeb.UI/Purchase/Transactions/PoOrderList.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor`

They consistently use:

- `PageBase`
- `MenuAuthorize`
- `iv-page`
- `iv-toast`
- `iv-hero`
- `iv-card`
- search above the grid
- FILTER popup
- `CommonDataGridEx`
- unique `GridKey`
- server/custom data source
- mobile compact list
- confirmation dialogs
- permissions resolved in code-behind

This is the strongest cross-module UI contract in the application.

### 2.2 Inventory entry pages form one family

Representative files:

- `IvStockTransfer.razor`
- `IvMiscIssue.razor`
- `IvMiscReceipt.razor`
- `IvGoodsReceipt.razor`
- `IvStockAdjustment.razor`
- `IvScrap.razor`
- `IvStockReturn.razor`
- `IvVendorReturn.razor`

Common characteristics:

- one Razor page for New/Edit/View;
- code-behind owns state and behavior;
- isolated CSS uses transaction-specific prefixes;
- compact `DxFormLayout` header;
- `DxGrid` for document lines;
- popup line editors and stock/item pickers;
- edit/remove controls hidden when read-only;
- sticky `Cancel | Save` or `Close | Edit` footer;
- dirty-state discard confirmation;
- NEW-state editing and service-side validation.

`IvStockCount` is a deliberate specialized workflow and should not be used as the default Inventory entry template.

### 2.3 Sales has a different, deliberate entry design

Representative files:

- `SaDocPage.razor`
- `SaDocPage.razor.css`
- `SaQt.razor`
- `SaSo.razor`
- `SaDo.razor`
- `SaInvoice.razor`
- `SaCdn.razor`
- `SaDocFlowPanel.razor`

Sales entry pages use a denser **commercial document** language:

- `SaDocPage` wrapper;
- `sdoc-titlebar`, not an Inventory-style icon hero;
- title + document/status context + KPI;
- grouped Document / Customer / Terms / References forms;
- billing/shipping tabs where relevant;
- detail grid and totals;
- revision/history behavior on quotation/order;
- document-flow panel;
- shipment/source-document extensions;
- e-Invoice panels for invoice/CN/DN where applicable;
- `SdValidationSummary`.

Forcing the Inventory entry hero/card layout onto these screens would reduce consistency, not improve it.

### 2.4 Purchase intentionally reuses the Sales document shell

The Purchase module is physically located under:

`ErpWeb.UI/Purchase`

and not `ErpWeb.UI/Procurement`.

Its `_Imports.razor` imports:

`ErpWeb.UI.Sales.Transactions`

Purchase document entry pages use `SaDocPage` / `sdoc-*`, including:

- `PoPr.razor`
- `PoOrder.razor`
- `PoInvoice.razor`
- `PoCdn.razor`
- `PoSbInvoice.razor`
- `PoSbCdn.razor`

This is intentional shared UI infrastructure.

Purchase then adds its own business interaction model:

- copy;
- revise;
- cancel;
- close;
- reopen;
- PR-to-PO sourcing;
- received/consumed line restrictions;
- vendor and ETA fields;
- permission-controlled cost visibility.

Creating a separate Purchase visual framework would be the wrong direction. Purchase should share the commercial-document shell but keep procurement-specific workflow behavior.

### 2.5 Production is a workflow workspace, not a normal header/detail document

Representative files:

- `PrWorkOrderEntry.razor`
- `PrMaterialIssueEntry.razor`
- `PrDailyProductionEntry.razor`
- `PrFinishedGoodReceiptEntry.razor`

Production execution screens consistently add:

- a command bar immediately below the hero;
- explicit operation/source selection phases;
- workspace phases;
- operation/work-order context that becomes locked;
- allocation/readiness rules;
- POST / ROLLBACK transitions;
- historical snapshot views;
- correction/reopen flows where applicable.

Examples:

- Daily Production: select operation -> entry workspace -> save -> POST -> optional ROLLBACK.
- Material Issue: select operation -> BOM/required material calculation -> allocation -> save/post.
- Finished Good Receipt: find eligible production source -> receipt workspace -> posting gate -> rollback/correction.
- Work Order: definition/snapshot/configuration -> draft -> release -> reopen only when allowed.

This interaction model should not be replaced by a generic popup-driven line editor.

---

## 3. Shared infrastructure that should be treated as authoritative

### Global CSS

`ErpWeb/wwwroot/css/site.css`

Owns:

- theme variables;
- light/dark values;
- Public Sans;
- required-field treatment;
- core surface/text/border tokens.

`ErpWeb/wwwroot/css/inventory-chrome.css`

Despite the name, it is now the effective shared **list-page chrome**:

- `iv-page`;
- hero/card;
- toasts;
- chips/status;
- filter toolbar;
- popup layout;
- skeleton;
- compact mobile list;
- responsive behavior.

`ErpWeb/wwwroot/css/pr-popup-grid.css`

Shared production picker/allocation table styling.

All of these are loaded by `ErpWeb/Components/App.razor`.

### Shared grid

`ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor(.cs)`

Important behavior:

- supports `CustomDataSource`;
- suppresses built-in search/filter/group UI for custom/server data;
- owns REFRESH behavior;
- persists layout by `GridKey`;
- strips stored column widths when restoring;
- strips filter criteria before saving layout;
- handles selection and toolbar/action callbacks.

Therefore new transaction list pages should not rebuild these capabilities manually.

---

## 4. Why an AI agent can ignore the current skill

### Problem A — too many concerns in one skill

One skill simultaneously describes:

- list pages;
- Inventory entry pages;
- Sales;
- Purchase;
- Production;
- workflow state;
- accessibility;
- CSS;
- permissions;
- mobile;
- performance.

An agent can satisfy many generic bullets while still choosing the wrong module anatomy.

### Problem B — reference hierarchy is too weak

“Use Inventory as the reference” does not force the agent to prove which live file it examined.

The new skills require an evidence block before coding.

### Problem C — class-name copying is not the same as UX standardization

Inventory entry pages already use different isolated prefixes such as:

- `tr-*`
- `mi-*`
- `mr-*`
- `sa-*`
- `sr-*`
- `vr-*`

The stable contract is the layout and behavior, not the exact class prefix.

### Problem D — Sales/Purchase shared infrastructure is missing from the generic mental model

`SaDocPage` is the strongest entry-page standard for commercial documents. It must be named explicitly.

### Problem E — Production has legitimate phase-based UI

Production routes and flows are intentionally different. A universal New/Edit/View template is insufficient.

### Problem F — no “stop/reject” gate

Guidance such as “should look consistent” is easy for an agent to interpret loosely.

The replacement skills add:

- archetype classification;
- required live references;
- evidence block;
- forbidden implementation patterns;
- module-specific Definition of Done.

---

## 5. Recommended standard

### Level 1 — shared core

Use `erp-ui-core`.

This controls:

- Razor/code-behind/CSS separation;
- DevExpress-only UI convention;
- shared list page;
- theme/accessibility;
- server paging;
- permissions;
- action semantics;
- dirty/submitting/loading/error states.

### Level 2 — module contract

Use exactly one primary module skill:

- `inventory-transaction-ui`
- `sales-transaction-ui`
- `purchase-transaction-ui`
- `production-transaction-ui`

The module contract determines entry anatomy and workflow.

### Level 3 — nearest live page

Within the module, choose the closest current production page as the concrete implementation reference.

The skill is a guardrail. The current repository is the final authority.

---

## 6. Recommended future refactors — optional, not required by these skills

These are architectural opportunities, not prerequisites for adopting the skills.

### 6.1 Rename/generalize `SaDocPage` later

Purchase already depends on a component located in the Sales namespace.

A future UI-only refactor could move it to a neutral location such as:

```text
ErpWeb.UI/Components/Common/Documents/ErpDocPage.razor
```

with its document chrome CSS beside it.

Do this only as a dedicated refactor because many pages currently depend on the existing component.

### 6.2 Consider a shared production workspace shell later

Daily Production and Finished Good Receipt contain similar `pr-dp-*` scoped CSS.

If more production execution pages are added, consider extracting stable workspace primitives rather than copying another isolated CSS file.

Again, this should be a separate refactor, not mixed into a functional feature.

### 6.3 Rename `inventory-chrome.css` only in a deliberate migration

It now serves more than Inventory lists, but renaming it casually creates unnecessary churn.

The skills intentionally use the existing file and classes.

---

## 7. Final recommendation

Replace the old monolithic skill with the router included in this package.

The best rule for future AI-generated UI is:

> **Standardize the interaction language, but choose the correct module archetype first. Inspect the current live reference, reuse existing shared primitives, and only change domain content. Never invent a fifth transaction UI style.**
