---
name: erp-transaction-ui-standard
description: >-
  Router for transaction UI work in mokth/net10projectTemplate. Use whenever creating,
  redesigning, reviewing, or standardizing an Inventory, Sales, Purchase/Procurement,
  Planning, or Production transaction list/entry page. This skill does not define a
  universal entry layout; it routes the agent to the shared core plus the correct
  module-specific UI skill.
---

# ERP Transaction UI Standard — Router

## Purpose

This skill is a **router**, not an implementation template.

The repository has one shared transaction-list language but multiple valid entry-page archetypes.

Do not implement a transaction UI by copying this file alone.

---

# 1. Mandatory routing sequence

Before writing or changing UI code:

1. Read:
   - `.agents/skills/erp-ui-core/skill.md`
2. Identify the target module from its actual repository path.
3. Read exactly one primary module skill:
   - Inventory -> `.agents/skills/inventory-transaction-ui/skill.md`
   - Sales -> `.agents/skills/sales-transaction-ui/skill.md`
   - Purchase / Procurement -> `.agents/skills/purchase-transaction-ui/skill.md`
   - Planning / Production execution -> `.agents/skills/production-transaction-ui/skill.md`
4. Inspect the live references required by that module skill on the current target branch.
5. Produce the module skill's **UI Standard Evidence Block**.
6. Only then implement.

If the task spans multiple modules, read every affected module skill, but still classify each changed page separately.

---

# 2. Repository terminology

Use repository terminology, not assumed business folder names.

| Business term | Repository UI path |
|---|---|
| Inventory | `ErpWeb.UI/Inventory` |
| Sales | `ErpWeb.UI/Sales` |
| Procurement / Purchase | `ErpWeb.UI/Purchase` |
| Production execution / work orders | `ErpWeb.UI/Planning/WorkOrders` |

Do not create a new `Procurement` or `Production` UI folder merely because the business request uses that word.

---

# 3. UI archetypes

Every transaction page must be classified before implementation.

## A. Transaction list

Default cross-module list pattern:

- `iv-page`
- `iv-hero`
- search + FILTER
- `CommonDataGridEx`
- compact mobile list
- confirmation/filter popup

Use the core skill plus the target module skill for business actions and filters.

## B. Inventory batch/header-detail entry

Use the Inventory skill.

Typical examples:

- Stock Transfer
- Misc Issue
- Misc Receipt
- Goods Receipt
- Stock Adjustment
- Scrap
- Stock Return
- Vendor Return

## C. Commercial document entry

Use Sales or Purchase skill.

The live repository uses:

- `SaDocPage`
- `sdoc-*`
- dense document titlebar
- grouped master/header form
- item grid
- totals
- commercial-document extensions

Do **not** replace this with an Inventory-style entry hero.

## D. Production workflow/workspace

Use Production skill.

Typical examples:

- Work Order
- Issue to Production / Material Issue
- Daily Production
- Finished Good Receipt

Production screens may have source-selection and workspace phases, command bars, readiness gates, posting and rollback.

Do **not** flatten them into a generic header/detail editor.

---

# 4. Reference priority

When references disagree, use this priority:

1. current code in the target page/family on the target branch;
2. current same-module canonical reference named by the module skill;
3. existing shared component/global CSS behavior;
4. module skill;
5. shared core skill;
6. old screenshots, old plans, or remembered patterns.

Never override proven current repository behavior because a prose skill contains an outdated detail.

If a live page has evolved beyond this skill, follow the live established pattern and record the difference in the evidence block.

---

# 5. Hard rejection rules

Reject the implementation before completion if it:

- creates a fifth transaction UI language;
- copies Inventory entry anatomy into a Sales/Purchase `sdoc-*` document;
- converts Production execution into a generic commercial-document form;
- invents a module folder that does not exist;
- puts normal transaction behavior in a large `.razor @code` block instead of code-behind;
- introduces a new UI library when DevExpress already solves the control;
- duplicates search/filter behavior already owned by `CommonDataGridEx`;
- loads a large transaction table into memory for convenience;
- ignores permission or document-state rules;
- hardcodes a light-only page palette;
- has no mobile/responsive behavior;
- declares UI complete without the module Definition of Done.

---

# 6. Final rule

> **One ERP interaction language, several deliberate transaction archetypes. First classify the page, then follow the correct live module reference. Reuse existing shared primitives. Change the business content, not the established interaction model.**
