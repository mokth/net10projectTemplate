---
name: sales-transaction-ui
description: >-
  UI/UX standard for Sales transaction list and commercial-document entry pages in
  mokth/net10projectTemplate. Use for quotation, sales order, delivery order, invoice,
  credit/debit note and related Sales document flows. Requires erp-ui-core and preserves
  the shared SaDocPage/sdoc document language.
---

# Sales Transaction UI Skill

## Required companion

Read first:

`.agents/skills/erp-ui-core/skill.md`

This skill owns Sales commercial-document behavior.

---

# 1. Actual repository location

Sales UI:

`ErpWeb.UI/Sales`

Transactions:

`ErpWeb.UI/Sales/Transactions`

---

# 2. Mandatory live references

Always inspect:

- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor.css`
- `ErpWeb.UI/Sales/Transactions/SdValidationSummary.razor`
- `ErpWeb.UI/Sales/Transactions/SaDocFlowPanel.razor`

For lists, inspect:

- `SaSoList.razor/.cs/.css`
- and the closest target document list

For entries, inspect the closest document:

| Document | Primary reference |
|---|---|
| Quotation | `SaQt.razor/.cs/.css` |
| Sales Order | `SaSo.razor/.cs/.css` |
| Delivery Order | `SaDo.razor/.cs/.css` |
| Sales Invoice | `SaInvoice.razor/.cs/.css` |
| Credit / Debit Note | `SaCdn.razor/.cs/.css` |

When e-Invoice is involved also inspect the existing `SaEInvoice*` transaction components.

---

# 3. Sales UI Standard Evidence Block

Before implementation:

```text
SALES UI STANDARD EVIDENCE
Target document:
List reference:
Entry reference:
SaDocPage used:
Source/upstream document(s):
Downstream document(s):
Modes:
Revision behavior:
Editable statuses:
Customer/address edit rules:
Price override authority:
Totals shown:
Posting/close/revise actions:
e-Invoice applicable:
Permissions:
Intentional deviations:
```

Do not code a new Sales document shell until this block is known.

---

# 4. Sales list page

Sales transaction lists use the shared `iv-*` list standard.

Do not create an `sdoc-*` list page.

Hero:

- eyebrow `Sales`;
- document title;
- useful count/filter chips;
- relevant document count KPI.

Use `CommonDataGridEx` with server/custom data.

Search/filter must match actual business fields.

Examples:

- SO number;
- customer;
- customer PO;
- date;
- status.

---

# 5. Sales entry shell — non-negotiable

Normal Sales document entry must use the current commercial-document shell:

```razor
<SaDocPage>
    ...
</SaDocPage>
```

Use `sdoc-*` classes owned by `SaDocPage.razor.css`.

Do **not** replace a Sales document entry with an Inventory-style icon hero + large 16px cards.

The current Sales entry language is intentionally denser.

---

# 6. Standard Sales entry anatomy

Use this order unless the live closest reference has a justified variation:

```text
SaDocPage
├─ success/error/validation feedback
├─ warnings/readiness messages when applicable
├─ sdoc-titlebar
│  ├─ H1 + document number
│  ├─ status/revision/fulfillment context
│  └─ total KPI
├─ optional revision/history strip
├─ sdoc-master
│  ├─ Document group
│  ├─ Customer group
│  ├─ Terms group
│  ├─ References group
│  └─ billing/shipping tabs when applicable
├─ optional source-document / shipment controls
├─ sdoc-items
│  ├─ Add/source action
│  ├─ DxGrid
│  ├─ empty state
│  └─ totals
├─ document-flow panel
├─ e-Invoice section when applicable
├─ sticky footer
└─ line/source/confirm/discard popups
```

Not every document needs every optional section.

---

# 7. Titlebar, not hero

The Sales entry titlebar is compact.

It communicates:

- mode/document identity;
- status;
- revision/fulfillment/billing context where applicable;
- one financial KPI such as Total incl. tax.

Do not add a decorative module icon to the entry titlebar merely to make it match Inventory.

Do not overload it with action buttons.

---

# 8. Header/master form

Use grouped compact `DxFormLayout`.

Common groups:

- Document
- Customer
- Terms
- References

Typical Sales concerns:

- document date/number/status;
- customer;
- currency/rate;
- payment terms;
- tax group;
- salesman;
- customer PO/reference;
- project/department;
- billing/shipping addresses.

Use current lookup components and SearchMode behavior.

Do not expose master-data internals as free text when a current lookup exists.

---

# 9. Billing / shipping tabs

Use the current tab pattern when the document owns address snapshots.

Rules:

- customer selection populates defaults through existing logic;
- stored document address is the transaction snapshot;
- editability follows current status/mode;
- do not silently re-read and overwrite saved document address in View;
- required e-Invoice identity/contact fields follow existing validation.

Do not add address tabs to a Sales document that does not own those address concepts.

---

# 10. Item grid and line editor

Use `DxGrid` with `sdoc-line-grid`.

Use:

- item code + description stack;
- quantity/UOM;
- price;
- discount;
- tax;
- amounts;
- source/reservation/fulfillment context where relevant.

Right-align numeric values.

Show line mutation actions only when `CanEditDocument`/`CanMutateLines`.

Use the current line popup/source-picker behavior from the closest document.

Do not replace the line model with a generic Inventory stock picker.

---

# 11. Pricing authority

Sales pricing is not a purely visual decision.

Before enabling unit-price edits, verify:

- current pricing engine resolution;
- user override permission;
- document/status rule;
- server-side override enforcement.

The current Sales Order family distinguishes whether a user can override the resolved price.

If override is not authorized:

- price control should be read-only;
- do not make it editable for convenience.

Always leave final enforcement in service/domain logic.

---

# 12. Validation presentation

Use the current Sales validation pattern.

Prefer:

`SdValidationSummary`

plus field-level errors.

Do not reduce a multi-field posting/validation problem to one generic toast if the existing family presents structured errors.

For posting readiness warnings, show actionable reasons.

---

# 13. Revision model

Quotation and Sales Order can have revision semantics.

When revision is supported:

- explicit `revise` mode may be valid;
- current vs historical revision must be clear;
- historical/superseded revision is read-only;
- revision reason belongs in the established UI;
- revision history should remain navigable.

Do not interpret an old revision as an editable current document.

Do not add a `revise` route to documents whose service has no revision model.

---

# 14. Source-document flow

Sales documents may be chained:

```text
Quotation -> Sales Order -> Delivery Order -> Invoice
```

and related CN/DN flows.

Use existing source pickers and `SaDocFlowPanel` where the target family does.

Document-flow display should be read-only context, not a substitute for source-selection validation.

Do not invent a new flow visualization if the shared panel already covers the requirement.

---

# 15. Sales list action families

Actions differ by document.

Observed patterns include:

### Quotation

```text
NEW
REVISE
DELETE
```

### Sales Order

```text
NEW
REVISE
FORCE CLOSE
DELETE
```

### Delivery Order

```text
NEW
POST
ROLLBACK
FORCE CLOSE
DELETE
```

### Invoice / CN / DN family

May include:

```text
NEW
POST
ROLLBACK
DELETE
SUBMIT
E-STATUS
CANCEL
```

plus e-Invoice/LHDN row/action functionality.

These are examples from the live family, not a universal toolbar recipe.

Use service/status/permission evidence.

---

# 16. Posting and e-Invoice

Posting and MyInvois submission are separate concerns.

Do not combine them into a single generic Save action.

For documents with e-Invoice:

- preserve existing post eligibility;
- preserve submit/cancel/status action model;
- show current e-Invoice state;
- use existing e-Invoice components;
- preserve warnings and validation;
- do not invent a parallel submission panel.

A document can be business-posted while having a distinct e-Invoice lifecycle. The UI must make that distinction understandable.

---

# 17. Footer

Editable:

```text
Cancel | Save
```

View:

```text
Close | Edit
```

Additional lifecycle actions should follow the closest live document pattern and should not casually crowd the Save footer.

Document totals belong in the established totals/title KPI, not repeated in multiple decorative cards.

---

# 18. Sales CSS rule

Shared commercial-document chrome belongs to:

`SaDocPage.razor.css`

Page `.razor.css` should contain only document-specific additions.

Example: Sales Order revision history belongs in `SaSo.razor.css`; the base titlebar, cards, line grid and footer do not.

Do not copy the whole `sdoc-*` CSS into each Sales page.

---

# 19. Sales rejection rules

Reject if the implementation:

- does not use `SaDocPage` for a normal Sales commercial document;
- creates an Inventory-style entry hero instead of `sdoc-titlebar`;
- duplicates shared `sdoc-*` CSS in the page;
- loses billing/shipping document snapshot behavior;
- makes historical revisions editable;
- makes price editable without verified override authority;
- omits document-flow context where the existing family uses it;
- merges e-Invoice submission with ordinary Save/Post incorrectly;
- copies the same toolbar actions to Quote, SO, DO and Invoice;
- presents only one generic error where structured validation is required.

---

# 20. Sales Definition of Done

- [ ] Core skill passed.
- [ ] Sales evidence block completed.
- [ ] `SaDocPage` / current `sdoc-*` shell inspected and reused.
- [ ] Closest document reference inspected.
- [ ] List uses shared `iv-*` list standard.
- [ ] Entry uses compact commercial-document anatomy.
- [ ] Customer/terms/reference/address sections match actual document needs.
- [ ] Price editability follows authority.
- [ ] Revision behavior is correct where applicable.
- [ ] Source/downstream flow remains understandable.
- [ ] Structured validation is visible.
- [ ] Totals are compact and consistent.
- [ ] e-Invoice UI is present only where applicable and uses existing components.
- [ ] Page-specific CSS contains only true page-specific styling.
- [ ] Mobile and dark-theme behavior remain valid.
