---
name: purchase-transaction-ui
description: >-
  UI/UX standard for Procurement/Purchase transaction list and commercial-document
  entry pages in mokth/net10projectTemplate. Use for Purchase Requisition, Purchase Order,
  Purchase Invoice, purchase credit/debit notes and self-billed documents. The actual
  repository module is ErpWeb.UI/Purchase. Requires erp-ui-core and intentionally reuses
  SaDocPage/sdoc shared document chrome.
---

# Purchase / Procurement Transaction UI Skill

## Required companion

Read first:

`.agents/skills/erp-ui-core/skill.md`

---

# 1. Repository naming — mandatory

Business users may say `Procurement`.

The UI module in this repository is:

`ErpWeb.UI/Purchase`

Do not create:

`ErpWeb.UI/Procurement`

unless a separate architectural change explicitly requests it.

---

# 2. Shared commercial-document dependency

Purchase `_Imports.razor` currently imports:

`ErpWeb.UI.Sales.Transactions`

Purchase entry pages intentionally reuse:

`SaDocPage`

and `sdoc-*` document chrome.

This is current repository behavior.

Do not create a new `pdoc-*` framework just to avoid the Sales namespace name.

A future neutral rename can be a dedicated refactor, but it is not part of normal Purchase feature work.

---

# 3. Mandatory live references

Always inspect:

- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor.css`
- `ErpWeb.UI/Purchase/_Imports.razor`

For lists, start from:

- `ErpWeb.UI/Purchase/Transactions/PoOrderList.razor`
- `PoOrderList.razor.cs`

Then inspect the target list.

For entries choose the closest:

| Procurement flow | Reference |
|---|---|
| Purchase Requisition | `PoPr.razor/.cs/.css` |
| Purchase Order | `PoOrder.razor/.cs/.css` |
| Purchase Invoice | `PoInvoice.razor/.cs/.css` |
| Purchase CN/DN | `PoCdn.razor/.cs` |
| Self-billed invoice | `PoSbInvoice.razor/.cs` |
| Self-billed CN/DN | `PoSbCdn.razor/.cs` |

---

# 4. Purchase UI Standard Evidence Block

Before implementation:

```text
PURCHASE UI STANDARD EVIDENCE
Business name:
Repository path:
Target document:
List reference:
Entry reference:
SaDocPage used:
Source document(s):
Modes:
Editable statuses:
Vendor edit lock:
Received/consumed line lock:
Cost-view permission:
Copy/revise behavior:
Cancel/close/reopen behavior:
Posting behavior:
Self-billed/e-Invoice applicable:
Permissions:
Intentional deviations:
```

---

# 5. Purchase list page

Use the same cross-module list shell:

- `iv-page`
- Inventory-style global list chrome
- `iv-hero`
- Purchase eyebrow
- search + FILTER
- `CommonDataGridEx`
- compact mobile list
- confirmations

This is intentional. Do not make the Purchase list visually mimic its `sdoc-*` entry page.

Search fields should reflect procurement work, e.g.:

- PR/PO/invoice number;
- supplier/vendor;
- buyer;
- item;
- date;
- status.

---

# 6. Purchase entry shell

Normal Purchase commercial documents use:

```razor
<SaDocPage>
    ...
</SaDocPage>
```

and `sdoc-*`.

Do not use an Inventory batch entry shell for PO/PR/invoice just because all are ERP transactions.

The shared document shell provides the cross-functional consistency between Sales and Purchase.

---

# 7. Standard Purchase document anatomy

Typical:

```text
SaDocPage
├─ feedback / validation
├─ sdoc-titlebar
│  ├─ document identity
│  ├─ status/revision
│  └─ total KPI when financial
├─ optional revision/history
├─ sdoc-master
│  ├─ Document
│  ├─ Vendor / Requester
│  ├─ Terms / Buyer / Authorization
│  └─ References / ETA / Project
├─ source-document picker/context
├─ sdoc-items
│  ├─ Add / Add from source
│  ├─ DxGrid
│  ├─ line issues
│  └─ totals if permitted/applicable
├─ optional document flow / self-billed e-Invoice
├─ sticky footer
└─ line/source/confirm/discard popups
```

Use only sections the document needs.

---

# 8. Procurement modes are document-specific

Do not force only New/Edit/View.

Current examples include:

### Purchase Requisition

```text
new
edit
view
copy
```

### Purchase Order

```text
new
edit
view
copy
revise
```

plus historical revision view.

### Invoice / CN/DN

typically:

```text
new
edit
view
```

Use the target service and current route model.

---

# 9. Purchase Requisition rules

PR is a request/sourcing document, not merely a lightweight PO.

The UI can include:

- requester;
- department/project;
- preferred/line vendor;
- authorization fields;
- ETA;
- warehouse;
- cost visibility;
- conversion/consumption state.

Existing lines already consumed into downstream procurement must not behave like ordinary editable lines.

Keep consumed lines visibly read-only and explain why.

Do not let UI-only edit controls bypass downstream-use restrictions.

---

# 10. Purchase Order rules

Purchase Order has richer lifecycle behavior.

Current list family includes actions such as:

```text
NEW
COPY
REVISE
CANCEL
CLOSE
REOPEN
DELETE
```

Use actual permissions and row eligibility.

Important interaction rules:

- supplier/vendor may become locked once receipt activity exists;
- historical revisions are read-only;
- closed/cancelled documents are read-only;
- close may require a reason;
- reopen is a deliberate state transition, not Edit.

Do not expose Edit as a substitute for Reopen.

---

# 11. Purchase Invoice / financial documents

Financial procurement documents may use:

- source PO/GRN matching;
- posting;
- rollback;
- cost/tax totals;
- self-billed e-Invoice rules.

Use current source/matching logic.

Do not redesign matching as generic free-form line entry when the current service expects sourced quantities/documents.

POST and ROLLBACK are lifecycle actions distinct from Save.

---

# 12. Cost visibility

Purchase contains cost-sensitive data.

Honor current `CanViewCost`/equivalent permission.

If user cannot view cost:

- hide or appropriately suppress cost/amount fields according to the existing page;
- do not leak cost through totals, tooltips, hidden DOM text or popup previews.

Cost visibility is not just a column preference.

Editable price/cost must still follow document state and business authority.

---

# 13. Source-document interactions

Procurement commonly flows:

```text
PR -> PO -> receipt/matching -> Purchase Invoice
```

and related returns/CN/DN.

Use existing source pickers and source-line eligibility.

Display:

- ordered;
- received;
- invoiced;
- remaining;

where the current domain supports those facts.

Do not let the user over-select source quantities and rely only on later service errors if the current UI can present eligibility earlier.

Service validation remains final.

---

# 14. Received / consumed line immutability

A line with downstream usage may have restrictions different from a new line.

UI must derive line actions from the line's actual eligibility.

Examples:

- consumed PR line;
- received PO line;
- matched/invoiced source quantity.

Do not show a normal enabled Delete button on a protected line.

Prefer explanatory title/help text when an action is unavailable.

---

# 15. Purchase list action families

Observed patterns include:

### PR

```text
NEW
COPY
CANCEL
DELETE
```

### PO

```text
NEW
COPY
REVISE
CANCEL
CLOSE
REOPEN
DELETE
```

### Purchase Invoice / CN/DN

```text
NEW
POST
ROLLBACK
DELETE
```

These are not interchangeable.

Never add an action solely to make Purchase pages have equal toolbar length.

---

# 16. Self-billed documents

Self-billed invoice/CN/DN are Purchase documents with additional compliance lifecycle.

Reuse the existing entry shell and established self-billed/e-Invoice components/state.

Do not build a second e-Invoice UI inside Purchase.

Keep business posting and MyInvois state distinguishable.

---

# 17. Purchase CSS rule

Base entry chrome lives in `SaDocPage.razor.css`.

Purchase page `.razor.css` should contain only genuinely Purchase-document-specific layout.

Do not duplicate `sdoc-*` base styles.

Do not create `purchase-chrome.css` merely to copy `SaDocPage`.

---

# 18. Purchase rejection rules

Reject if:

- UI code is placed under `ErpWeb.UI/Procurement` without an explicit architecture change;
- a normal Purchase document does not use the current shared `SaDocPage` shell;
- `sdoc-*` base CSS is duplicated into a Purchase page;
- cost is shown to a user without the applicable permission;
- consumed/received lines remain freely editable/deletable;
- Copy and Revise are treated as synonyms;
- Edit bypasses Closed/Cancelled/Historical state;
- Reopen is implemented as client-side status text change;
- a financial document's source/matching model is replaced by free-form lines without service evidence.

---

# 19. Purchase Definition of Done

- [ ] Core skill passed.
- [ ] Purchase evidence block completed.
- [ ] Actual `ErpWeb.UI/Purchase` path used.
- [ ] `SaDocPage` and closest Purchase reference inspected.
- [ ] List follows shared `iv-*` list standard.
- [ ] Entry follows shared commercial-document shell.
- [ ] PR/PO/invoice lifecycle is not generalized incorrectly.
- [ ] Copy/revise/cancel/close/reopen semantics are correct.
- [ ] Received/consumed line restrictions are visible and enforced.
- [ ] Cost visibility permission is honored everywhere.
- [ ] Source-document quantities/eligibility are understandable.
- [ ] Posting/rollback remains distinct from Save.
- [ ] Self-billed/e-Invoice behavior reuses existing components.
- [ ] Page-specific CSS contains only page-specific rules.
- [ ] Responsive and dark-mode behavior remain valid.
