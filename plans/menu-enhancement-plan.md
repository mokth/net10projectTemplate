# Menu Enhancement Plan — Left Sidebar (NavMenu)

**Status:** Study / design only — **implementation blocked** until revised plan is approved and the full approval package (Section 16) is complete  
**Date:** 2026-07-12  
**Revision:** R2 — incorporates owner review (8.8/10, approve with revisions)  
**Scope:** Professionalize ErpWeb left navigation (density, icons, order, IA)

---

## 1. Goal

Turn the current sidebar into a professional ERP-style navigation that is:

- Compact and scannable (font size, row height, gaps)
- Icon-led (level 1–2 required; level 3 optional per icon policy)
- Ordered by real user workflows (not accidental / demo clutter)
- Consistent with the rest of the app (accent, light/dark)
- Safe for existing permissions, routes, and menu sync (no regressions)

**Deliverable of this plan’s study phase:** findings + target design + file change map + option choice (A/B/C) + regression checklist.  
**Code starts only after** the approval package in Section 16 is complete and the owner selects an option.

---

## 2. Scope

### In scope

| Area | What we study |
|------|----------------|
| Look & density | Font, casing, row height, padding, selection, hover, focus, nesting |
| Icons | Source, mapping, missing data in `menus.xml`, policy by tree level |
| Information architecture | Top-level order, group labels, depth, demos vs utilities — **only after full tree inventory** |
| Behavior | Active route selection, auto-expand, persistence, sidebar collapse, empty groups |
| Permissions | Existing filtering must not regress (no permission redesign) |
| Theme / a11y | `--nav-*` vs `--accent`, light/dark, contrast, focus/keyboard |
| Data sync | XML → DB → cache → UI path for Icon and SortOrder |

### Out of scope (until a separate decision)

- Permission / role matrix redesign (existing behavior must be preserved and tested)
- Full mobile navigation redesign (desktop density targets only; note mobile gaps only)
- Backend/API redesign beyond menu data delivery
- Deleting demo menu definitions (visibility only — see Section 4.1.3)

---

## 3. Current state (baseline — for verification, not final truth)

> **Review note:** Section 3 items below are **initial findings to verify**. Phase 0–1 must convert each into Confirmed / Not confirmed / Partially confirmed / Requires runtime verification before Phase 2 finalizes target IA.

### 3.1 Primary files (observed from source — confirmed by file read)

| Role | Path |
|------|------|
| Markup | `ErpWeb.UI/Components/Layout/NavMenu.razor` |
| Styles | `ErpWeb.UI/Components/Layout/NavMenu.razor.css` |
| Load | `ErpWeb.UI/Components/Layout/NavMenu.razor.cs` |
| Shell | `ErpWeb.UI/Components/Layout/MainLayout.razor` (+ `.css`, `.cs`) |
| Menu data source | `ErpWeb/Menus/menus.xml` |
| Model | `ErpWeb.Core/Menus/MenuNavItem.cs` (`Icon`, `SortOrder`, …) |
| Load tree | `ErpWeb.Core/Menus/MenuService.cs` |
| Permission filter | `ErpWeb.Core/Menus/NavigationService.cs` |
| Sync XML → DB | `ErpWeb.Core/Menus/MenuSyncService.cs` |
| Theme tokens | `ErpWeb/wwwroot/css/site.css` |
| Icons CSS (page chrome only) | `ErpWeb/wwwroot/css/icons.css` |
| FontAwesome | `ErpWeb/Components/App.razor` (FA 6.5.2 CDN — confirm runtime load) |

### 3.2 Initial findings to verify

Classify every row during Phase 0–1 as: **Confirmed** (source) · **Runtime** (browser) · **Hypothesis** · **Design rec** (target only).

| ID | Class | Finding (claim) | How to confirm |
|----|--------|-----------------|----------------|
| F1 | Confirmed (source) | `DxTreeView` maps `IconCssClass` ← `MenuNavItem.Icon` | Read `NavMenu.razor` |
| F2 | Confirmed (source) | `menus.xml` currently has **zero** `Icon` attributes | Read full XML |
| F3 | Confirmed (source) | `MenuSyncService` copies `Icon` and `SortOrder` | Read sync code |
| F4 | Confirmed (source) | FontAwesome referenced in `App.razor` | Read host |
| F5 | Confirmed (source) | CSS forces uppercase, `min-height: 34px`, `padding: 6px`, full purple selected block | Read `NavMenu.razor.css` |
| F6 | Confirmed (source) | Nav purple tokens ≠ global `--accent` blue | Read `site.css` |
| F7 | Confirmed (source) | Top-level order: Home, Inventory, Sales, Purchase, Operations, Security, Change password | Read `menus.xml` SortOrder |
| F8 | Hypothesis / runtime | “Icons missing in UI because data empty” | Browser after load; inspect tree icons |
| F9 | Hypothesis / UX | Order feels “not ERP” for real users | Compare F7 inventory vs target IA after Phase 2 |
| F10 | Hypothesis / UX | Density feels “too big / airy” | Browser measure row height + visual review |
| F11 | Hypothesis / behavior | Active path auto-expand may be incomplete | Phase 1 route cases (Section 5) |
| F12 | Hypothesis / behavior | `MenuExpansionState` may not be wired to `NavMenu` | Phase 1 code + runtime |
| F13 | Design rec | Target order / icons / pin utilities | Only after Phase 0–2 inventory complete |

**Do not treat F8–F13 as implementation-ready P0s until verified.**

### 3.3 Known architecture strengths (keep)

- Nested IA skeleton (module → Master / Transactions / Analysis) is a reasonable ERP base
- Icon pipeline exists end-to-end at the model/mapping layer
- Change channels already separated: CSS vs data vs markup vs full redesign

### 3.4 Current top-level order (`menus.xml` — source snapshot; must be re-exported in full inventory)

| SortOrder | Code | Name | Notes |
|-----------|------|------|--------|
| 1 | `HOME` | Home | Quick link — **do not remove until route semantics verified** |
| 2 | `INVENTORY` | Inventory | First business module |
| 3 | `SALES` | Sales | |
| 4 | `PURCHASE` | Purchase | |
| 5 | `OPERATIONS` | Operations | Overview → Dashboard + Inventory Demo |
| 6 | `SECURITY` | Security | Administration subtree |
| 7 | `CHANGE_PASSWORD` | Change password | Utility — pin candidate |

> **Hard rule:** Phase 1 inventory (Section 4.1) is the **authoritative** tree. This snapshot is not sufficient for final IA.

---

## 4. Phase plan (revised order per review)

```
Phase 0  Baseline + complete menu inventory
            ↓
Phase 1  Runtime / route / permission verification
            ↓
Phase 2  ERP information architecture  (blocked until 0–1 done)
            ↓
Phase 3  Visual + density design
            ↓
Phase 4  Behavior + accessibility
            ↓
Phase 5  Icon / data delivery verification
            ↓
Phase 6  Target design package (A–E artifacts)
            ↓
Approval gate  (owner picks A/B/C + signs package)
            ↓
Implementation  (only after approval)
            ↓
Phase 7  Regression validation
```

---

### Phase 0 — Baseline + complete menu inventory (mandatory before IA)

| # | Task | Source | Output |
|---|------|--------|--------|
| 0.1 | Re-read layout + CSS + load path | NavMenu.*, MainLayout.* | Issues with file:line |
| 0.2 | **Export complete menu inventory** | `menus.xml` + DB after sync + runtime tree | Table (below) |
| 0.3 | Classify F1–F12 | Source + short runtime | Confirmed / Not / Partial / Runtime |
| 0.4 | Theme tokens | `site.css` | Color notes |
| 0.5 | Shell constraints | `--nav-w`, collapse | Density budget (desktop) |

#### 4.1 Hard dependency: full menu inventory before Phase 2

**Phase 1 (IA) cannot finalize** until every node is listed with:

| Column | Required |
|--------|----------|
| Code | yes |
| Name | yes |
| Parent code | yes |
| Depth | yes |
| Route | yes (or “container / no route”) |
| Icon | yes (null or value) |
| SortOrder | yes |
| AlwaysVisible / permission behavior | yes |
| Container vs navigable page | yes |
| Demo / test? | yes |
| Duplicate or overlapping routes? | yes |
| Notes | optional |

**Phase 0 done when:** inventory complete + findings classified.  
**Phase 2 blocked if inventory incomplete.**

---

### Phase 1 — Runtime / route / permission verification

#### 4.1.1 Permission behavior (first-class acceptance — no redesign)

Changing order, moving nodes, or pruning must not regress `NavigationService` filtering.

**Permission matrix (representative roles):**

| Case | Expected |
|------|----------|
| Full access | All permitted nodes visible |
| No child permission | Empty parent **hidden** |
| One child permitted | Parent remains visible; only that child shown |
| Active page permitted | Page selected; full parent chain expanded |
| Active page not permitted | No invalid selection; no orphan highlight |
| Demo permission disabled | Demo node hidden |
| Container with no permitted descendants | Not shown |

Also verify: XML vs DB vs runtime cache consistency after icon/order sync.

#### 4.1.2 Route / active-state acceptance cases

Current mapping uses `NavigationUrlMatchMode.Prefix` — confirm intended behavior before coding expand logic.

| # | Case | Expected |
|---|------|----------|
| R1 | Navigate `/sales` | Selects Sales (module) |
| R2 | Navigate `/sales/invoices` (actual routes from inventory) | Selects leaf; expands Sales → Transactions (or real parents) |
| R3 | Deep URL e.g. `/sales/.../detail` or id in path | Still selects correct leaf if prefix match is intentional |
| R4 | Two routes with overlapping prefixes | **Must not** select the wrong node |
| R5 | Direct browser load of deep URL | Full parent chain expanded |
| R6 | F5 / refresh | Active state and expansion preserved per defined rule |
| R7 | Navigate module A → module B | Collapse/expand rule applied consistently (define: keep prior open vs collapse others) |
| R8 | Page not in menu / no permission | No false selected state |

**Concrete case list must use real routes from Phase 0 inventory**, not assumptions.

#### 4.1.3 Home vs Dashboard

**Do not delete or rename either node during design.**

Resolve only after measuring:

- Home route vs Dashboard route  
- Which is landing after login  
- Bookmarks / default route  
- Authorization on each  
- Whether they are the same screen or different  

| Decision flag | “Resolve after route/landing-page verification” |

#### 4.1.4 Demo handling — hide, do not delete

- Demo menu **definitions remain** in `menus.xml` for development/testing unless a **separate cleanup decision** is made.
- Production visibility controlled by **environment / permission / feature configuration** (or equivalent), not by deleting definitions.
- Plan language: **“Hide from normal production users”** ≠ “Remove node from XML”.

**Phase 1 done when:** permission matrix designed + route cases use real paths + Home/Dashboard verified + demo policy stated.

---

### Phase 2 — ERP information architecture (after 0–1)

#### 4.2.1 Pattern checklist (target mental model)

| Pattern | Typical ERP rule | Current (pending inventory confirmation) |
|---------|------------------|------------------------------------------|
| Top quick links | Dashboard / Home only | Home + Dashboard under Operations |
| Business modules | Inventory / Sales / Purchase as peers | Present |
| Master vs Transactions | Clear children | Present; labels generic |
| Analysis / Reports | Under module or global | Sales → Analysis only |
| System / Admin | Near bottom | Security present |
| Utilities | Pinned | Change password peer of modules |
| Demo items | Hidden in prod | Visible in XML structure |
| Depth | Prefer max 2–3 | Security → Admin → Master → leaf = 4 |

#### 4.2.2 Candidate top-level IA (proposal only — finalize after inventory)

*Candidate order (not approved):*

1. Dashboard **or** Home — **only after §4.1.3**  
2. Inventory  
3. Sales  
4. Purchase  
5. Administration / Security  
6. **Utilities (pinned):** Change password (+ Logout later if added)

**Children must come from the real inventory**, not assumptions. Example shape only:

```
Dashboard
Inventory
├── Master
├── Transactions
└── (Analysis if real screens exist)
Sales
├── Master
├── Transactions
└── Analysis
Purchase
├── Master
└── Transactions
Administration
└── ...
Utilities (pinned area)
└── Change Password
```

#### 4.2.3 Naming test (required)

Replacement labels must pass:

> A user should understand what is inside a group **without opening it**.

Validate against **actual child screens**:

| Name option | Risk |
|-------------|------|
| Master | Familiar to ERP users if children are Customers, Suppliers, Items |
| Setup / Configuration | Implies system config — wrong if children are business masters |
| Reference Data | Accurate for lookups; may feel academic |

Do not rename solely because it “sounds professional.”

#### 4.2.4 Utility pin layout model (structure proposal — not implementation)

```
NavMenu
├── SidebarHeader          (optional; header may live in shell)
├── ScrollableNavigation
│   └── DxTreeView
└── SidebarUtilities
    └── Change Password    (and Logout later if added)
```

Must specify in behavior/design: scroll area vs pin, viewport height, collapsed sidebar, mobile, keyboard order, dark mode.

**Phase 2 done when:** target tree uses full inventory; naming test applied; utilities layout model written.

---

### Phase 3 — Visual + density design (desktop)

#### 4.3.1 CSS targets

| Property | Current (source) | Target (desktop ERP) |
|----------|------------------|----------------------|
| Casing | `text-transform: uppercase` | Sentence / title case |
| Font size | `0.8rem` (~13px) | **13px** stable |
| Row min-height | `34px` + `6px` pad | **~30–32px** (top-level may be ~36px if needed) |
| Vertical padding | `6px` | **2–4px** |
| Selection | Full purple fill | **Left accent bar** + light tint |
| Hover | Purple wash | Neutral + accent text |
| Focus | Not specified | Visible focus indicator |
| Accent | Purple `#5e418a` vs `--accent #2f6fed` | Owner choice — default proposal: align `--accent` |
| Group headers | Same as leaves | Muted / section label optional |
| Nesting indent | Tree default | Consistent ~12–16px |
| Bottom utility | In scroll | Pinned area (see 4.2.4) |
| Width | `--nav-w: 248px` | Keep unless icon-rail designed |

**Explicitly desktop-only density.** Do not apply 30–32px rows to mobile without a separate mobile pass.

#### 4.3.2 Change channel matrix

| Change | Channel |
|--------|---------|
| Casing, padding, height, selection, hover, focus, icon size | CSS (`NavMenu.razor.css`, maybe `site.css`) |
| Icons, order, labels, demo visibility flags | Data (`menus.xml` → sync) + config as needed |
| Pinned utilities, section labels | Markup/structure (`NavMenu.razor`, shell) |
| Custom rail instead of TreeView | Full component redesign (Option C) |

#### 4.3.3 Accessibility / contrast (numeric checks)

Validate and record pass/fail:

- Body text contrast (light + dark)  
- Selected-state text vs background  
- Hover text vs background  
- Focus ring visibility on tree items  
- Keyboard navigation through expanded/collapsed nodes  
- Click/touch target height note (desktop density OK; mobile out of scope)

**Phase 3 done when:** token table + contrast checklist drafted (pass/fail after runtime check in Phase 4/7).

---

### Phase 4 — Behavior + accessibility (runtime)

| # | Check | Output |
|---|--------|--------|
| 4.1 | Active route cases R1–R8 | Pass/fail |
| 4.2 | Auto-expand active parents | Pass/fail |
| 4.3 | Expand state persistence (`MenuExpansionState` or equivalent) | Wired / not wired |
| 4.4 | Sidebar collapse behavior | Current hide vs future rail — document only |
| 4.5 | Loading / empty groups after prune | No empty parents |
| 4.6 | Keyboard / a11y | Checklist |
| 4.7 | Long labels, deep routes | Truncation / overflow |

---

### Phase 5 — Icon / data delivery verification

```
menus.xml (Icon, SortOrder, hierarchy)
  → MenuDefinitionService
  → MenuSyncService (dbo.Menu)
  → MenuService (cache)
  → NavigationService (permission prune)
  → NavMenu DxTreeView IconCssClass
  → FontAwesome (App.razor)
```

#### Icon policy (explicit)

| Level | Policy |
|-------|--------|
| Level 1 (top-level) | **Required** for production-visible nodes |
| Level 2 (meaningful groups) | **Required** for Master / Transactions / Analysis / Admin groups |
| Leaf pages | **Optional** (priority lower) |
| Utility actions (pinned) | **Required** if visually separated from tree |
| Demo / test nodes | Optional (or hidden in prod) |
| Classes | **No invented icon classes** — only classes present in loaded FA version |

**Also:** verify exact FontAwesome version **actually loaded at runtime** before approving individual icon names.

Seed SQL that inserts menus without icons must be noted if it can override/skip XML icons.

**Phase 5 done when:** icon policy accepted + proposed `Icon=` table + sync path verified.

---

### Phase 6 — Target design package + approval gate

#### Approval package must contain exactly these artifacts (A–E)

**A. Current-state inventory**  
One table: Code, Name, Parent, Depth, Route, Icon, SortOrder, Permission/visibility, Demo?  
→ Authoritative baseline.

**B. Target-state tree**  
Full tree; children from real system (Section 4.2.2 shape is illustrative only).

**C. Visual specification**  
Exact: font size, row height, padding, indentation, icon size, selected, hover, focus, light theme, dark theme, sidebar width.

**D. Behavior specification**  
Active route matching, parent expansion, persistence, collapse, scroll, utility pinning, empty group handling, Home vs Dashboard decision.

**E. Regression checklist**  
Explicit tests that must pass (Section 7).

Plus: **File change map** and **Option A/B/C selection** (owner).

#### Options (revised — do not pre-select winner)

| Option | Scope | Use when |
|--------|--------|----------|
| **A — Quick Win** | CSS + icons + XML ordering/labels | Existing structure is sufficient |
| **B — Structural** | A + utility area + active-path/expand improvements + demo visibility handling | Current structure needs modest changes |
| **C — Full Redesign** | Custom navigation component | `DxTreeView` cannot meet **verified** requirements |

> **Review correction:** Option B is a **candidate — select after Phase 0–4 findings**, not “recommended” up front.

**Implementation blocked until:** A–E approved + option chosen + open decisions (Section 6) closed.

---

### Phase 7 — Regression validation (after any implementation)

Minimum coverage:

| Area | Checks |
|------|--------|
| Navigation | Every permitted menu item opens correct route; no broken links; no duplicate active selection; no empty groups |
| Permission | Filtering unchanged; restricted pages still inaccessible; matrix cases pass |
| Data sync | XML icon/order reaches DB; existing rows updated; sync **idempotent** |
| UI | Light; dark; expanded; collapsed; long names; deep routes; empty/filtered groups |
| Browser | Supported desktop browsers for this app |
| Automated tests | **Must pass:** `MenuDeploymentParityTests`, `NavigationServiceTests`, `MenuExpansionStateTests` (+ any related menu/nav tests) |

Implementation is **not complete** until Phase 7 checklist is green.

---

## 5. Route acceptance (quick reference for agents)

Use real routes from inventory. Placeholder examples only:

| ID | Case | Expected |
|----|------|----------|
| R1 | Module URL | Module selected |
| R2 | Leaf list URL | Leaf selected; parents expanded |
| R3 | Deep URL with id/query | Correct leaf if prefix match intended |
| R4 | Overlapping prefixes | No wrong selection |
| R5 | Cold deep link | Full parent chain expanded |
| R6 | Refresh | Active + expand state per rule |
| R7 | Cross-module nav | Expand/collapse rule applied |
| R8 | Unauthorized / unmapped | No false selection |

---

## 6. Open decisions (must be closed before coding)

| # | Question | Status | Default if unspecified |
|---|----------|--------|------------------------|
| 1 | Home vs Dashboard (after route verification) | **Open — resolve in Phase 1** | Do not delete either during design |
| 2 | Module order Inventory→Sales→Purchase vs Sales-first | Open | Keep current order unless product says otherwise |
| 3 | Change password pinned bottom vs under Administration | Open | Pinned bottom (layout model 4.2.4) |
| 4 | Demo visibility mechanism | Open | Hide in prod; **do not delete definitions** |
| 5 | Accent: `--accent` vs brand purple | Open | Align to `--accent` (proposal only) |
| 6 | Option A / B / C | **Open — after Phase 0–4** | None (no pre-selection) |

---

## 7. Success criteria (measurable)

- [ ] All production-visible top-level business modules have icons (policy §5)
- [ ] Menu order matches the **approved** target tree (inventory-based)
- [ ] No demo/test menu visible to normal production users (definitions retained)
- [ ] No empty parent group after permission filtering
- [ ] Active route selects **exactly one** appropriate node (R1–R8)
- [ ] Active route auto-expands all required parents
- [ ] Selected state uses approved accent treatment (not full heavy block unless approved)
- [ ] Light and dark themes meet approved contrast requirements (checklist)
- [ ] Sidebar usable at approved desktop width (`--nav-w` or approved change)
- [ ] Change Password visually separated from business modules (if pin chosen)
- [ ] `MenuDeploymentParityTests`, `NavigationServiceTests`, `MenuExpansionStateTests` pass
- [ ] No existing valid route broken by navigation change
- [ ] Desktop density targets not claimed for mobile (mobile out of scope)

---

## 8. Execution order (when study runs)

| Step | Task | Done when |
|------|------|-----------|
| 1 | Phase 0 inventory + classify F1–F12 | Full table + statuses |
| 2 | Phase 1 permission + route + Home/Dashboard + demo policy | Matrix + R-cases + decisions |
| 3 | Phase 2 target IA (from real children) | Tree proposal |
| 4 | Phase 3 visual tokens + a11y checklist | Numeric spec |
| 5 | Phase 4 behavior runtime pass | Pass/fail |
| 6 | Phase 5 icons/sync | Icon table + path verified |
| 7 | Phase 6 package A–E + option | Owner approval |

---

## 9. Final deliverable before coding (owner handoff)

Implementation agent receives **only after approval**:

1. Revised approved plan (this file)  
2. Complete current menu inventory (Artifact A)  
3. Approved target menu tree (Artifact B)  
4. Approved visual tokens (Artifact C)  
5. Approved behavior rules (Artifact D)  
6. Approved option (A / B / C)  
7. Regression checklist (Artifact E)  

**Until all seven are fixed: no code changes.**

---

## 10. File change map (future — not started)

| File / area | Likely change | Option |
|-------------|---------------|--------|
| `ErpWeb/Menus/menus.xml` | Order, labels, `Icon=`, demo visibility (not delete) | A/B/C |
| `ErpWeb.UI/Components/Layout/NavMenu.razor.css` | Density, casing, selection, hover, focus | A/B/C |
| `ErpWeb/wwwroot/css/site.css` | Optional `--nav-*` / accent align | A/B/C |
| `ErpWeb.UI/Components/Layout/NavMenu.razor` (+ `.cs`) | Pinned utilities, expand wiring, sections | B+ |
| DB via menu sync | Icon/SortOrder from XML | A/B/C |
| Seed SQL under `scripts/` | Only if inserts omit icons/order | As needed |
| Custom nav component | Replace TreeView | C only |
| Demo visibility mechanism | Env/permission/feature flag | B+ |

**Not in first pass:** `MenuNavItem` shape (Icon already exists); replacing FA package without runtime version check.

---

## 11. Out of first delivery

- Production code commit before Section 9 complete  
- Permission seed redesign  
- Icon-only collapsed rail (optional follow-up)  
- Global logout UX (coordinate with header later)  
- Mobile density redesign  

---

## 12. Next action

1. Close or explicitly defer **Section 6** decisions that Phase 1 can answer (Home/Dashboard, demo mechanism).  
2. Run **Phases 0–5** → publish package A–E.  
3. Owner picks **Option A/B/C** (no pre-selection).  
4. Implement only after Section 9’s seven inputs exist.  
5. **Phase 7 regression** must pass before “done.”

---

## Appendix A — `NavMenu.razor.css` pressure points (source — verify still accurate)

```text
::deep .nav-tree … font-size: 0.8rem
… text-transform: uppercase
… min-height: 34px; padding-top: 6px; padding-bottom: 6px
… selected: full background --nav-selected-bg + font-weight 600
… hover: --nav-hover-bg
```

## Appendix B — Icon mapping already in markup (source)

`NavMenu.razor` includes `IconCssClass="Icon"` — icons should appear once `menus.xml` supplies `Icon` and sync runs; confirm in browser (F8).

## Appendix C — Related tests (must pass)

- `ErpWeb.Tests/MenuDeploymentParityTests.cs`  
- `ErpWeb.Tests/NavigationServiceTests.cs`  
- `ErpWeb.Tests/MenuExpansionStateTests.cs`  
- Related: `MenuDefinitionServiceTests`, `MenuSyncServiceTests`  

## Appendix D — Related docs / code

- `ErpWeb/docs/menu-access.md`  
- `ErpWeb.Core/Menus/MenuCodes.cs`  
- `ErpWeb.Core/Menus/NavigationService.cs` (permission prune)  
- `ErpWeb.Core/Menus/MenuSyncService.cs`  

## Appendix E — Review response map

| Review item | Where addressed |
|-------------|-----------------|
| Separate confirmed vs hypothesis | §3.2 F1–F13 |
| Full tree before IA | §4.1 hard dependency; Phase 0 before Phase 2 |
| Permission first-class | §4.1.1 matrix |
| Route acceptance cases | §4.1.2, §5 R1–R8 |
| Home vs Dashboard | §4.1.3, Decision #1 |
| Pin layout model | §4.2.4 |
| Hide vs delete demos | §4.1.4, Decision #4 |
| Icon policy L1–L3 + FA version | §5 |
| Naming test | §4.2.3 |
| A11y contrast + desktop-only density | §4.3.1, §4.3.3 |
| No pre-select Option B | §6 options table |
| Regression phase | Phase 7, Appendix C |
| Revised phase order | §4 diagram |
| Artifacts A–E | Phase 6 / §9 |
| Measurable success criteria | §7 |
| Seven-item handoff | §9 |
