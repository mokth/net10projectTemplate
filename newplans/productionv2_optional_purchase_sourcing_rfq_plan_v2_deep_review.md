# PRODUCTIONV2 — OPTIONAL PURCHASE SOURCING, RFQ & SUPPLIER QUOTATION COMPARISON
## Implementation-Ready AI Coding Agent Plan — V2.1 (Gate 0 Verified Against Local Workspace & Approved for Phased Execution)

**Plan status:** **APPROVED FOR PHASED IMPLEMENTATION (V2.1 — GATE 0 CODEBASE & BUILD VERIFICATION COMPLETED).** Strict static source review and local workspace Gate 0 inspection completed against `c:\wincom\productionv2`. Phases 1–5 remain governed by hard per-phase exit gates (Gates A–G).  
**Repository:** `mokth/net10projectTemplate`, branch `productionv2`.  
**Local workspace HEAD verified on 2026-11-13:** `960cbeb1a388af50589e095c3caa461d5c6502e0` (13 commits ahead of prior review pin `e24f54069316a346171fed7f14017c09d593cb95`; zero changes to `PoOrderService.cs` or `PoPrService.cs` in those 13 commits; baseline `ErpWeb.Tests.csproj` build verified with 0 errors).  
**System:** ASP.NET Core / Blazor + DevExpress, EF Core + SQL Server, multi-company / tenant-scoped ERP.  
**Module:** Procurement. **Feature classification:** Optional, value-added, **company-level OFF by default**.  
**Absolute safeguard:** Do not change inventory quantity, stock balance, WIP, FIFO/WAC/standard valuation, CostLayer, stock posting/reversal, GRN/PI costing, or month-end rules. Any identified unavoidable core change requires a separate written proposal and explicit user approval.

---

## 0. Strict review outcome, severity and mandatory corrections

**Static & Gate 0 codebase review rating: 9.8/10 for V2.1 executable specification; approved for phased implementation under hard Gates A–G.** During Gate 0 inspection of the local `c:\wincom\productionv2` workspace, all V2.0 assumptions were verified against exact file and line numbers, and 5 additional local-code blockers in `PoPrService`, `PoOrderService`, and `AdSmParam` were identified and resolved below. These V2.1 corrections supersede any softer wording later in this document:

| Priority | V1/V2.0 risk / repository evidence | Mandatory V2.1 correction |
|---|---|---|
| **CRITICAL** | Existing `PoOrderService.SaveNewAsync` and `PoPrService.SaveNewAsync` own their own DB context, transaction and document number. Simply calling one from `PoSourcingConversionCoordinator` and then writing a source link would be non-atomic. | Source reference/quantity claim and target creation **MUST** share the target's existing transaction and DB context, with a narrowly inserted hook after the native validation/locks and before commit; rollback both together. If that cannot be proven, **STOP Phase 4**. |
| **CRITICAL** | **Local Gate 0 Finding #1:** `PoPrService.PrepareLinesAsync` (`ErpWeb.Core/Purchase/PoPrService.cs:1943-1961`) ignores `src.UnitPrice` on new lines (`sameICode == false`) and unconditionally overwrites `unitPrice` via `ResolveUnitPriceAsync(...)` from `PoVendorByItem` or `IvStockMaster`/`PoPurItem`. | Authorize a narrowly scoped sourcelink-aware check in `PoPrService.PrepareLinesAsync`: when converting from an approved sourcing award snapshot (or when an explicit sourced positive `src.UnitPrice > 0m` is supplied under the in-transaction sourcing context), preserve `RoundPrice(src.UnitPrice)` instead of overwriting it via `ResolveUnitPriceAsync`. Ordinary non-sourced PR entry remains unchanged. |
| **HIGH** | `PoOrderService.PrepareLinesAsync` (`ErpWeb.Core/Purchase/PoOrderService.cs:1676-1678`) for a newly created PO uses vendor/master fallback when `PoUnitPrice == 0` (unless same-item prior line). | No silent reinterpretation of a legitimate zero-value supplier quote. Record/compare zero-price quotes, but block automatic zero-price conversion unless a separately reviewed, scoped explicit-zero-price contract is approved and tested. Do not modify existing general pricing fallback. |
| **HIGH** | `PoOrderService.EnforceDocumentMode` (`PoOrderService.cs:1774-1840`) rejects mixing inclusive/exclusive tax and one-time/stock lines in one PO. **Local Gate 0 Finding #2:** `PoPrService.EnforceCurrencyInclusive` (`PoPrService.cs:2053-2080`) also rejects mixing currencies or inclusive/exclusive tax lines in one PR. | Target PO grouping splits by `(CompanyCode, BranchCode, VendCode, Currency, IsInclusive, OneTimeItemYn)`. Target PR grouping splits by `(CompanyCode, BranchCode, Currency, IsInclusive, PrType)` (multiple `VendorCd` values may share one PR only when `Currency` and `IsInclusive` match). Preflight target grouping and present expected PR/PO count. Preserve original validations. |
| **HIGH** | `PoOrderService.PrepareLinesAsync` requires a valid `ICode` resolvable in Stock or Purchase Item master; free-text description alone is not a valid PO line. **Local Gate 0 Finding #3:** `PoOrderService.SaveNewAsync` (`PoOrderService.cs:582-747`) does not enforce on the server that `request.VendCode` is an active `PoSupplier` (only `PoOrder.razor.cs:197` checks `CanSave` in the UI). | Capture quote-only free-text goods/services and quote-only supplier candidates for comparison, but block automatic PR/PO conversion until a valid item/purchase-item reference exists, and **explicitly validate on the server inside the conversion transaction that `VendCode` exists and has `IsActive == true` in `db.PoSuppliers` for `(CompanyCode, BranchCode)`** before creating a PO. |
| **HIGH** | `SearchPrForPoAsync` (`PoOrderService.cs:2302-2343`) and `BuildPrRemainingLinesAsync` (`PoOrderService.cs:2393-2538`) allow blank-vendor PR lines (`string.IsNullOrEmpty(x.Detail.VendorCd) || x.Detail.VendorCd == normalizedVendor`), while save-time `ValidateAndApplyPrConsumptionAsync` checks status/approval/quantity but does not itself validate supplier match. | Provide **sourcing-only source resolution** with explicit rules: blank PR vendor may be assigned to awarded supplier after authorization; a nonblank DIFFERENT PR vendor requires permitted PR amendment or denial. Enforce identity/vendor mapping on server in the conversion claim, not merely picker UI. Never globally relax ordinary PO picker. |
| **HIGH** | **Local Gate 0 Finding #4:** `PoOrderService.SynchronizeDetails` (`PoOrderService.cs:1872-1897`) deletes and re-inserts all `PoOrderDetail` rows (`Line = 1..N`) on every `UpdateAsync`; `PoPrService.SynchronizeDetails` (`PoPrService.cs:2153-2168`) deletes and re-inserts all `PoPrDetail` rows when `!hasConsumed`. | Because `PrepareLinesAsync` captures `PreparedLine.ExistingLineNo` from the incoming DTO `Line`, the in-transaction sourcing hook in `PoOrderService.SaveExistingAsync` (update/revise) and `PoPrService.UpdateAsync` **must** consume the deterministic `(OldLineNo -> NewLineNo)` map produced by `SynchronizeDetails` / `ReviseAsync` to atomically remap `PoSourcingConversionLine` line numbers and mark any deleted `OldLineNo` as released/detached. |
| **HIGH** | `AppSettingService.GetFlagAsync` uses a cached setting snapshot (60-second process-local cache). **Local Gate 0 Finding #5:** `AdSmParam` (`AdSmParamConfiguration.cs:19-51`) defines `CompanyCode` and `BranchCode` as `nvarchar(5)`, and `AppSettingService.ClearAsync` (`AppSettingService.cs:343-385`) deletes the `AdSmParam` row. Locking `AdSmParam` last after document locks risks lock-order inversion. | UI may read cached flag via `IPurchaseSourcingFeatureGuard`; **all new-source writes/awards/conversions must acquire the authoritative `AdSmParam` `UPDLOCK, HOLDLOCK` check FIRST inside the SQL transaction** (before PO revision, item lifecycle, PR, or award locks), using `nvarchar(5)` key parameters on `(ModuleCode = N'PROCUREMENT', ParamKey = N'SOURCING_ENABLED', ScopeCode = N'COMPANY', CompanyCode, BranchCode = N'')`. Default/missing/deleted/invalid row fails closed. |
| **HIGH** | Multi-supplier output may require several independent PR/PO saves. | One target document + its claims is atomic. Across different suppliers, **each target is independently atomic and idempotent**; expose partial progress. Never advertise group all-or-none. |
| **MEDIUM** | Scope may include sourced PR → PO without direct sourcing token, or normal PO revisions after converting. | Trace PR-derived POs by actual `(Company, Branch, PrNo, PrLineNo)` and latest live PO revision (`PoRelNo == MAX(PoRelNo)` and `Status != CANCELLED`, matching `PoPrService.ComputeLockedProcurementCoverageAsync` at `PoPrService.cs:1713-1732`); do not double count PR commitment plus its downstream PO. |
| **MEDIUM** | Existing procurement approval engine is PR/PO scoped, not RFQ scoped. | New sourcing award sign-off uses its own simple `APPROVE` permission and event history; **never auto-approve or bypass PR/PO**. Separate award approver policy is deferred unless already provided by permission configuration. |

**Evidence standard:** File/line citations below refer to the local `productionv2` workspace at `960cbeb1a388af50589e095c3caa461d5c6502e0`. Phase 4 remains governed by explicit runtime concurrency and transaction tests (Gates C, D, E).

---

## 1. Executive decision

Add **Purchase Sourcing & Quotation Comparison** as an optional capability within **existing Procurement**, not a separate ERP or mandatory purchasing flow. Companies that never enable it must see and experience the existing PR → PO → GRN → Purchase Invoice workflow exactly as before. Enabling it adds an RFQ/Quotation/Comparison/Award workbench and optional **Create PR** and **Create PO** actions. Existing purchasing controls remain the final authority.

**Company setting:** `PROCUREMENT.SOURCING_ENABLED` (type `Flag`, allowed scope `Company`, default `false`). Implement via existing `AppSettingCatalogue` and `IAppSettingService`/`AppSettingService`; **no Company table column or new settings service**. Display to an administrator as **“Supplier Quotation Comparison”** with description **“Allow buyers to collect supplier quotations, compare offers and create PR/PO. Turn off to use normal purchasing only.”** Company-level only: branch cannot override in v1.

**This is a switch for availability, not an entitlement or license check.** Commercial licensing/packaging may be layered on later only if an existing entitlement framework is confirmed. Do not fabricate licensing infrastructure.

### Invariants
1. **Disabled company:** sourcing navigation, create controls and sourcing write endpoints unavailable; direct existing PR/PO remains unchanged; previously recorded sourcing data and audit are retained, not erased. Existing PR/PO and their downstream transactions remain usable.
2. **Enabled company:** sourcing tools operate only within signed-in company/branch and granted menu/transaction permissions; user cannot use a bookmarked route or forged request when feature is off.
3. **Sourcing alone has zero stock, WIP, accounting, cost-layer, ledger, posting or period-close effects.** Quote/estimated landed cost is *non-posting purchasing analysis*.
4. **PR/PO creation from an award must run the existing PR/PO validation, numbering, approval and quantity-consumption logic.** Never reimplement these in a parallel source-to-PO writer.
5. All award quantities, conversions and document references are transactionally consistent; retries and concurrent users cannot create duplicate or over-allocated documents.
6. Historical RFQs, supplier quote revisions, approval decisions and awards remain auditable even if a quote expires, the award is changed, a resulting PR/PO is cancelled, or the company disables the feature.
7. Feature enable/disable is **not** a license or entitlement guard. Do not introduce product billing checks without a separate approved design.
8. Source-linked existing PRs/POs must remain fully operable through their native validation and downstream lifecycle **after feature disable**. Only **new sourcing activities** stop; source-link status maintenance remains internal, read-only/metadata, and never bypasses normal PR/PO rules.

---

## 2. Verified current repository baseline (do not assume missing functionality based on old chats)

Source inspection on `productionv2` found:

| Existing pattern | Current source | Reuse/constraint |
|---|---|---|
| PR entity, detail, attachments, service and UI | `ErpWeb.Model/Entities/Purchase/PoPr.cs`, `PoPrDetail.cs`; `ErpWeb.Core/Purchase/PoPrService.cs`; `ErpWeb.UI/Purchase/Transactions/PoPr.razor(.cs)` | Existing PR creation, quantity conversion, tax and validation. PR details include optional vendor and PR quantities. |
| PO entity, detail, save, revision and attachments | `PoOrder.cs`, `PoOrderDetail.cs`; `PoOrderService.cs`; `PoOrder.razor(.cs)` | Existing PO supplier, pricing, revision, numbering and PR link. PO detail has `(PrNo, PrLineNo)`; PO header has legacy `QuatationNo` free text, **not** a supplier-quote-comparison implementation. |
| PR→PO search/remaining quantity | `PoOrderService.SearchPrForPoAsync`, `GetPrRemainingLinesAsync`, `LockConsumptionAsync`, `ValidateAndApplyPrConsumptionAsync`, `StampPrConsumptionAsync` | **Critical**: existing PO import filters PR by `VendorCd` if a PO vendor is chosen. It also locks PR rows and validates PR status/approval/remaining quantity in the PO save transaction. Do not bypass or duplicate these. |
| PR create transaction | `PoPrService.SaveNewAsync` | Uses SQL transaction and document numbering; end-of-transaction source linking must be atomic. |
| PO create transaction | `PoOrderService.SaveNewAsync` | Uses SQL transaction and PR consumption locks; source award link is a narrow *additive hook* inside this transaction, not a replacement save path. |
| PR/PO approval | `ProcurementApprovalService.cs`, `ProcurementApprovalSqlLocks.cs`, `ProcurementApprovalFingerprint.cs`, PR and PO UI | Submitted/approved documents are protected. Sourcing sign-off **does not replace** existing PR/PO approval. |
| Supplier catalog & per-item vendor | `PoSupplierService.cs`, `PoVendorByItem.cs`, supplier pickers | Reuse lookup, supplier profile, vendor part number, lead time, purchase UOM and buyer where applicable. Do not automatically overwrite master price. |
| Actual purchase price history | `ErpWeb.UI/Purchase/Inquiry/PoPriceHistoryInquiry.razor(.cs)` and `PoPurchaseInquiryService` | Existing history uses posted purchase invoice lines. The UI comment explicitly notes **no FX/UOM conversion** for that inquiry. Normalize *within sourcing* without changing history calculations. |
| Settings framework | `AppSettingCatalogue.cs`, `AppSettingModules.cs`, `AppSettingService.cs`, `IAppSettingService.cs`; `AdminSettings.razor(.cs)` | Add one company-scope flag in the catalogue and `All` list. Orphan rows are ignored until registered in catalogue. |
| Menu/permissions | `ErpWeb/Menus/menus.xml`, `MenuCodes.cs`, `NavigationService.cs`, `AccessRightService.cs`, `MenuSyncService.cs`, `PermissionCodes.cs` | Menus are XML/DB synchronized and filtered by permission, **but current navigation does not filter by a company feature flag**. Add narrowly scoped flag-aware pruning for sourcing only and enforce service-side. |
| SQL deployment conventions | `scripts/create-po-pr.sql`, `scripts/create-po-approval.sql`, `scripts/create-po-order.sql` | Follow manually deployed, additive, idempotent SQL scripts; **no auto-DDL at startup**. |
| EF model/config | `ErpWeb.Model/Data/AppDbContext.cs`, `ErpWeb.Model/Configurations/Purchase/` | Register new sourcing entity mappings with correct tenant keys and decimal precision. |
| Service registration | `ErpWeb.Core/CoreServiceCollectionExtensions.cs` | Extend existing dependency injection, do not create alternate app composition. |
| Existing tests | `ErpWeb.Tests/Procurement/Transaction/PoOrderSqlServerConcurrencyTests.cs`, `PoPrSqlServerConcurrencyTests.cs`, `ProcurementApprovalSqlServerConcurrencyTests.cs`; settings tests | Add SQL Server concurrency, conversion, approval and feature-disabled regressions alongside established tests. |

**Additional confirmed facts (critical to source mapping):** `PoOrderService.PrepareLinesAsync` needs a valid item master/purchase item code, applies `PoOrderCalc`, performs master-price fallback for zero-value newly entered lines and rejects mixed inclusive/one-time modes (`EnforceDocumentMode`). `PoOrderService` can rewrite/resequence lines during updates and creates a fresh PO revision while cancelling a prior revision. `PoPrService` has its own transaction + line renumbering and supports nullable per-line supplier. PR/PO create DTOs (`IPoPrService.cs`, `IPoOrderService.cs`) have no sourcing context today. New source requests must not be trusted to supply final line number/quantity mapping. `AppSettingService.cs` has 60-second in-process cache; do not use that alone for disable-safe new writes.

**No dedicated RFQ/Quotation Comparison entities, services, pages or menu appeared in the inspected Purchase module folders.** This is a scoped source inspection, not a claim that every file was exhaustively audited.

**Preflight unknowns RESOLVED in Gate 0 against local workspace (`960cbeb1a388af50589e095c3caa461d5c6502e0`):**
- **PO Supplier Mandatory Rule:** Enforced in UI (`ErpWeb.UI/Purchase/Transactions/PoOrder.razor.cs:197`, `CanSave`), while `PoOrderService.SaveNewAsync` (`ErpWeb.Core/Purchase/PoOrderService.cs:582-747`) does not currently reject an unregistered/blank `VendCode`. Sourcing conversion MUST explicitly validate `db.PoSuppliers` (`CompanyCode`, `BranchCode`, `SuppCode == VendCode`, `IsActive == true`) on the server inside the conversion transaction.
- **Currency Rate Resolver:** `ICompanyCurrencyRateResolver` / `CompanyCurrencyRateResolver` (`ErpWeb.Core/StockLedger/CompanyCurrencyRateResolver.cs:30-74`) resolves `Company.CurrencyCode` and queries `db.SaCurrRates` (`CurrCode == normalizedCurrency && Status && StartDate.Date <= effectiveDate && EndDate.Date >= effectiveDate`, ordered by `StartDate DESC`, rounded to 8 decimals).
- **UOM & Pack Conversion:** `PoOrderService.PrepareLinesAsync` (`PoOrderService.cs:1684-1692`) and `PoPrService.PrepareLinesAsync` (`PoPrService.cs:1963-1980`) compute `StdQty = RoundQty(purchaseQty * packSz)` using `src.PackSz > 0m ? src.PackSz : resolved.PackSz` (`IvStockMaster`/`IvItemPackPolicy`), rather than reading `IvItemUomConversions` (`IvUomConversionService.cs:63-160`). Therefore, sourcing conversions must supply verified `PurchaseUom`, `StdUom`, and `PackSz` directly in `PoPrLineDto` / `PoOrderLineDto`.
- **PR Blank-Vendor → PO Behavior:** `PoOrderService.SearchPrForPoAsync` (`PoOrderService.cs:2302-2343`) and `BuildPrRemainingLinesAsync` (`PoOrderService.cs:2393-2538`) already include PR lines where `string.IsNullOrEmpty(x.Detail.VendorCd) || x.Detail.VendorCd == normalizedVendor`.
- **Attachment Security Pattern:** Follow `PoOrderAttachmentService` (`ErpWeb.Core/Purchase/PoOrderAttachmentService.cs:1-250`) with `AttachmentStorageOptions`, server-generated relative storage paths, extension/size whitelist, tenant/branch check, and additional `VIEW_COST` gate on quote attachment download.
- **Numbering:** `IDocumentNumberingService` (`ErpWeb.Core/Numbering/DocumentNumberingService.cs:1-250`) with `AdSmNum` / `AdSmNumDate` (`DocType = "RFQ"` or fallback sequential format under transaction lock).
- **UI Shell:** `PoSourcingList.razor` uses `iv-page` / `iv-hero` / `iv-card` / `CommonDataGridEx` (`ErpWeb/wwwroot/css/inventory-chrome.css`) + `erp-inline-search`; `PoSourcingWorkbench.razor` uses `SaDocPage` (`ErpWeb.UI/Sales/Transactions/SaDocPage.razor`).

---

## 3. Supported business workflows

The feature must be **flexible and optional**. Never force all companies or users into RFQ.

### A. Existing normal purchasing (feature OFF or unused)
`New PR → Approve (if required) → PO → GRN → Purchase Invoice`; where permitted, `New PO → GRN → Purchase Invoice`. **Zero extra clicks.**

### B. Sourcing before PR (requested by user)
`New sourcing request → Add requirements → Invite/record quotations from multiple suppliers → Compare → Select supplier(s) → Create PR draft(s) → Existing PR approval → Existing PR→PO flow → GRN/Invoice`.

### C. Sourcing directly to PO (optional)
`New sourcing request → Quotes → Compare → Award → Create PO draft(s) via existing service → Existing PO approval → GRN/Invoice`. Direct PO only if the same company's pre-existing PO permission/policy permits direct PO. Sourcing is **not an approval bypass**.

### D. PR first, then RFQ (conventional SME workflow)
`Existing PR → Start RFQ for eligible remaining PR lines → Compare → Award → Create vendor-specific PO(s) against the same approved, remaining PR lines`. Reuse existing PO PR consumption, approval checks and latest-revision rules. **Critical edge case:** source PR line vendor may be blank; current `SearchPrForPoAsync` **and** `BuildPrRemainingLinesAsync` exclude it when PO supplier is selected. The normal PO save guard does **not** enforce this exact supplier match, so sourcing must validate vendor consistency independently. Implement a **source-aware, permission-checked selection path** or carefully scoped allowance only after proving PR status, source identity, supplier award and quantity; do **not** globally loosen normal PR picker filtering. If existing PO validation fundamentally prohibits vendor-blank PR conversion, stop at Gate 0 with a minimal proposed change and targeted regression tests.

### E. Ad hoc offers (no formal outgoing RFQ)
Buyer may create a sourcing case and enter quotations already obtained from **email, WhatsApp, supplier PDF, telephone, website, marketplace or in person**, including source URL/reference, timestamp and attachments. Formal RFQ sending is not required.

### F. Partial and split awards
Line 1 may go to supplier A, line 2 to B, or 100 units may split A=60 and B=40. Create separate POs by supplier (and by currency/other hard PO grouping constraints). A single PR may be suitable for multiple item-line vendors if current PR rules allow; otherwise group PR drafts safely by required header rules. Remaining amounts stay available for subsequent valid conversions, with no double-counting.

---

## 4. Minimum production feature set

### 4.1 RFQ / sourcing request
- New/View/Edit/Copy/Cancel; source from existing PR or enter directly; optional production work order, project, department, required date, delivery warehouse/notes using existing lookups; distinguish sourcing lines from stock reservation.
- Header: unique source number, company/branch, title, requester/buyer, date, reply deadline, expected delivery location, project/department, remarks, status, created/modified audit and rowversion.
- Lines: numbered item (existing SKU or explicitly allowed one-time/service description), quantity, requested UOM, pack size/base quantity via existing conversion rules, specifications, delivery target, notes, original PR ref (if any), vendor-neutral requirement snapshot. No stock availability or valuation write.
- Supplier invitation list: existing supplier lookup plus a **quotation-only candidate** (supplier name/contact), without immediately creating `PoSupplier`. Supplier contact details are not proof of a valid payable vendor.
- A **free-text RFQ requirement** is allowed for discovery/quotation only. Both current PR/PO preparation paths require a supported item or purchase-item identity for conversion. Conversion must pause with a specific "Select or create a valid purchase item" message until that reference exists; NEVER create a placeholder stock item to make conversion succeed.
- A single-source comparison or one quotation is valid: multiple competing quotes are recommended, **not mandatory**; show "Only one supplier quoted" prominently. RFQ sending, web portal and email reply automation are not release blockers.
- Optional printable/email-shareable RFQ document. Use existing reporting/attachment/email patterns only if verified; sending automation is not essential for initial release.

### 4.2 Supplier quotation capture
- Record each supplier's quote number, date, expiry, revision, currency, contact, payment terms, delivery/freight, quote origin/channel, document attachment, timestamp and buyer notes.
- Line offers: vendor part/specification, quoted quantity/UOM/pack, unit price, discount, tax treatment, lead time/ETA, MOQ, warranty/quality terms, validity and exceptions.
- Allow partial quotes: suppliers may decline some RFQ lines; distinguish `NO_QUOTE` from `0.00` price. **Zero is valid as a quotation value, but current PO save logic can replace a zero unit price with the supplier/master price for a new line; reject auto-conversion of zero-priced award until a narrowly scoped, separately reviewed solution exists.** Multiple revisions must be identifiable and old revisions remain readable.
- Attachment supported for PDF/images and safe allowed office formats per current storage policy. Never accept arbitrary file paths or untrusted public URLs as storage paths. Enforce tenant and owner/edit authorization, file-size/type validation and authenticated download.
- Quote-only supplier is valid for comparison; before PO creation, require the *existing* PO supplier/one-time supplier policy to be satisfied. Do not create master suppliers silently.

### 4.3 Side-by-side comparison
- Comparison at **same product specification, base unit and requested quantity**, with clear `Not comparable` status if required data is missing or mismatched.
- Show raw currency/unit price alongside **normalized local-currency/base-UOM comparable price** (quote snapshot FX rate/date, verified UOM factor), discount, tax and incoterm/buying term, freight, charges and lead time. Separate: **goods value**, **estimated landed/comparable cost**, and **estimated tax-inclusive payable total**. Taxes recoverable/nonrecoverable and other adjustments must not be silently conflated.
- Freight may be quote-header level. Show total on quote, and use an explicit, deterministic allocation basis only for item-level comparison (or mark quote-level only). Do not allocate into actual CostLayer / GRN costing.
- Support supplier-specific quoted UOM/pack size and MOQ; conversion uses a read-only verified conversion source; reject ambiguous/missing conversions. FX must use an explicit dated rate or be uncomparable—**no FX=1 fallback** except identical currencies.
- Historical **posted invoice** prices may be shown as reference with source, date, currency and original UOM. Do not present unmatched historical prices as directly comparable or alter the existing inquiry.
- Per-line recommendation: lowest *comparable total*, shortest lead time and preferred supplier; editable simple weight preferences if justified later. Do not claim one supplier is objectively “best” without comparable inputs.
- Buyer can choose a non-lowest offer with mandatory justification; store comparison snapshot, selected quote revision, selection reason, user/time. Exclude expired/withdrawn/noncompliant quotes unless authorized exception with reason and audit.

### 4.4 Award and conversion
- Finalize selections (“Award”) with **explicit `APPROVE` permission on the sourcing menu**, distinct from PR/PO approval; first release can use single-step sign-off, no new configurable multi-level workflow engine. `REJECT` can return it for rework. Avoid naming this 'Posted': sourcing never posts inventory.
- Freeze selected supplier/quote revision, price/currency, quantity/UOM, terms, comparable breakdown and award reason as immutable award snapshot. Amend via a linked new award revision, not overwrite past decisions.
- Buttons: **Create PR**, **Create PO**, **View PR/PO**, **Remaining to Convert**. Buttons only enabled for valid status, feature on, target-document permission, non-expired eligible award, and remaining allocatable quantity. Server independently validates all checks.
- **Create PR**: prepare `PoPrSaveRequest`/`PoPrLineDto` from **server-stored award snapshots**; only invoke the native `PoPrService.SaveNewAsync` entry point **with a verified in-transaction source hook**. If source selected a registered supplier, populate vendor per PR line; if a quote-only supplier has no valid supplier code, keep vendor blank only when compatible with real PR validation, explicitly flag required supplier setup before PO. Do not claim free-text items can become PR lines without a valid item identity. Created PR is NEW and goes through existing approvals.
- **Create PO**: populate existing `PoOrderSaveRequest`/`PoOrderLineDto` from **server-stored award snapshots** and use the native `PoOrderService.SaveNewAsync` flow **with an in-transaction source hook**; group by supplier and every actual PO document-mode constraint, including currency, tax-inclusive/exclusive mode, one-time/stock mode, and applicable header rules; preserve PO supplier requirement, PR link, ordering quantity, tax rules, numbering and approval. Created PO is NEW and goes through existing approvals. **Never create a posted GRN, invoice or stock movement from sourcing.**
- For PR-first flow, PO links through the current `(PrNo, PrLineNo)` and downstream PR consumption logic; do not produce a second source allocation for the same quantity when the PR-linked PO is generated.
- Before conversion, show **target preview** with supplier, actual target currency/UOM, tax mode, per-line converted price/qty and resulting document count. If native target pricing/rounding differs from approved quote, **stop and require permissioned explanation**; do not silently approve an incorrect supplier amount. The posted/received cost remains governed by normal downstream purchasing rules.
- Retain a **source award → generated PR/PO → latest valid PO revision → GRN → PI** inquiry trail. Source links are *metadata*, not substitutes for target document status, quantity or cost authority.

### 4.5 Inquiries
- Source status: draft, awaiting quotations, comparison, award pending, awarded, partially converted, converted, cancelled, expired (calculate derived statuses where appropriate).
- RFQs awaiting response; quote expiry; comparison decisions; supplier win/loss and quote turnaround; converted PR/PO and remaining award qty; source documents and attachments.
- Read-only export to Excel/CSV using existing authorized export patterns. Redact negotiated prices, quote costs, supplier-sensitive details on server projections and exports if `VIEW_COST` is denied; do not rely on hidden grid columns. **Original supplier quote attachments (PDF/Excel/images) may contain prices and cannot be safely field-redacted; deny download/preview to users lacking source quotation price-view permission**, rather than returning raw files.

### Deferred / explicitly out of v1
Supplier web portal; automatic multi-supplier email chasing; AI extraction of scanned quotes/OCR; e-auction/tender management; internet price scraping; vendor score auto-learning; contract pricing updates; ERP accounting entries; new procurement approval engine; autonomous supplier master updates; changes to historical purchase price inquiry or costing engine.

---

## 5. User experience and DevExpress UI contract

**User goal:** buyer should be able to enter 3 vendor quotes for a simple RFQ and compare them without navigating through many document pages.

One **Purchase Sourcing Workbench** using responsive three-step tabs/panels:
1. **Request** — header + editable requirement grid, optional *Import PR Lines*, supplier invitation selector.
2. **Supplier Quotations** — compact vendor tabs/cards; paste/keyboard-friendly line-grid entry; copy line descriptions/spec from RFQ; attach quote; one Save/Cancel control group.
3. **Compare & Award** — side-by-side grid by RFQ line, highlighted minimum *comparable cost*, supplier selection per line, editable awarded qty, reason, total and approvals; **Create PR / Create PO** buttons after award sign-off.

UI rules: use current `PoOrder`/`PoPr` `SaDocPage` titlebar, `DxFormLayout` SizeMode.Small, existing supplier and item pickers, established toolbar/Save/Cancel and confirmation styles; put row action buttons on **left**; fixed sensible field widths (quantity, price, UOM, currency, date, supplier); never require memorizing item or supplier codes; no oversized panels; horizontal scroll only for genuine comparison grid with sticky item header and supplier columns; meaningful tablet layout (supplier cards or horizontally scrollable comparison, not a 20-column squeezed grid). Do not invent an independent styling system.

Plain-language labels and help:
- “Request Prices” / “Record Quote” / “Compare Offers” / “Choose Supplier” / “Create PR” / “Create PO”.
- “Can't create PO: supplier is not registered or allowed as a one-time supplier.”
- “Two users tried to allocate the same remaining quantity. Refresh to see the latest result.”
- “This quotation expired. Choose another quote or request an exception.”
- “Supplier prices cannot be compared until their units/currency rates are confirmed.”

Display feature in Procurement navigation only when enabled and authorized. **Hide is not an authorization control**: direct URL, service API, attachment API and exports must also deny when disabled or unauthorized. **No additional clicks or changes on existing PR/PO screens for disabled companies.** For enabled companies, use an unobtrusive “Import from Sourcing” action as needed; preserve standard New PR/New PO entry.

---

## 6. Setting, lifecycle and disabling behavior

1. Register `ProcurementKeys.SourcingEnabled = "SOURCING_ENABLED"` and `ProcurementSourcingEnabled` as `AppSettingType.Flag`, `AppSettingScope.Company`, `"false"`, add to `AppSettingCatalogue.All`; use `AppSettingModules.Procurement`. Existing admin settings UI will display the flag. Add concise help if existing generic UI supports it.
2. Add a company-aware feature guard `IPurchaseSourcingFeatureGuard` (or similar small service), resolve through existing settings and tenant scope, fail closed on unknown/unavailable setting. **Admin/SYSTEM_ADMIN permission bypass does not override the company OFF feature flag**.
3. The feature flag is company-level; company A ON/company B OFF in the same DB must be isolated; separate databases also isolated. Branch-scoped records stay branch-scoped; company setting only controls availability.
4. In `NavigationService` add small, targeted filtering of `PO_SOURCING` menu codes using the guard, without changing existing menu cache semantics across tenants. **Do not toggle the shared menu DB `IsActive` per company**; the menu definition stays globally installed and is pruned at request/tenant level. Avoid caching ON/OFF state globally or using menu `IsActive` as a per-company toggle. Add menu sync/permission tests.
5. On **each new** RFQ/quote/award/conversion, revalidate flag inside its SQL transaction against **authoritative company-scope `AdSmParam` row**, not only `IAppSettingService.GetFlagAsync` (60-second cache). Require exact active valid `true` under the catalogue's company-scope semantics. When absent/unrecognized/false, fail closed. A transient lookup failure must never be taken as true.
6. **Disable/create linearization contract:** In Gate 0 inspect the exact setting row key and SQL Server update semantics. Proposed implementation: a transaction-held `UPDLOCK,HOLDLOCK` on the existing enabled company row during source writes. An administrator's ordinary `AppSettingService.UpsertAsync`/`ClearAsync` then competes on that same row lock. Verify with a two-session SQL Server race that either the new-source commit happens first and then disable, **or** disable commits first and the new-source write is denied. For a different DB/provider or if this cannot be proven, introduce a **sourcing-only company feature lock** at *both* the setting writer and new source writer, confined to this exact setting (no generic settings rewrite), or STOP. Document any IIS instance cache delay for page/nav; server write remains authoritative.
7. Feature-toggle authorization applies to admin settings via existing `ADMIN_SETTINGS EDIT`; enable only after per-tenant sourcing tables and role permissions are verified. A schema readiness check cannot change source status or auto-migrate anything.
8. Disable later: block new RFQ, edits, awards and conversions; retain rows, files, audit and PR/PO links; do not auto-cancel PR/PO or purge data. Existing normal PR/PO remains usable. **Internal reconciliation of already-linked PR/PO revisions, cancellation and downstream status must continue even when sourcing is OFF**, so the toggle never blocks normal document changes or corrupts historical trace; it must not allow new source awards or conversions. Re-enable restores access to historic source documents subject to permissions and statuses. Security/support retrieval must remain possible under an appropriately authorized, explicit administrative process, not a public hidden route.
9. If settings table/catalogue unavailable in a company database, fail closed **for sourcing only**; no break to existing PR/PO.

---

## 7. Proposed additive database/entities

Use small, clear **`PoSourcing*` tables** with company/branch scope and versioned references. Exact columns, lengths, PKs, FK/index names to be finalized in Gate 0 according to current schema conventions; do not exceed existing PR/PO line size/precision when converting. No migration of old PRs/POs and no new columns in stock, cost or inventory tables.

| Entity/table | Minimum purpose / key fields |
|---|---|
| `PoSourcingHdr` | `SourcingId` (surrogate) + unique `(CompanyCode, BranchCode, SourcingNo)`; title, source type, requester, dates, PR source optional, lifecycle, audit, `rowversion`. |
| `PoSourcingLine` | `SourcingLineId`; `SourcingId`, sequence, item/spec, requested purchase/base quantities and UOM/pack snapshot, required-by, optional `(PrNo, PrLineNo)`; unique sourcing line ordinal. |
| `PoSourcingSupplier` | invited/considered supplier or quote-only candidate; supplier code optional, snapshot name/contact, response status; no forced master creation. |
| `PoSourcingQuote` | quote ID, supplier ID, supplier reference, quote revision, dates/expiry, currency/rate snapshot, terms, transport/charges, status, audit, rowversion. A revised quote creates a new version or immutable superseded snapshot, not silent overwrite after award. |
| `PoSourcingQuoteLine` | link quote to source line; offered qty/UOM/pack, price, discounts, tax, lead days/ETA, MOQ, specs, exceptions and normalization snapshot. |
| `PoSourcingAward` / `PoSourcingAwardLine` | award version + selected quote-line revision, supplier, quantity, conversion basis, reason, approver/date, comparison snapshot, immutable award history and rowversion for active award header. |
| `PoSourcingConversion` / `PoSourcingConversionLine` | **Immutable operation identity** `(CompanyCode, BranchCode, RequestId)` (unique, also stores stable payload hash), source award line, first target document type/ID/line, quantity in awarded base unit, and native-target snapshot. Persist original creation event and subsequent append-only adjustment/release events; do not overwrite historical conversion. Live effective state is resolved from **PR/PO current data** and latest PO revision, never by summing every revision. Use appropriate rowversion for mutable operational state only; no unguarded mutable counter. |
| `PoSourcingAttachment` | secure metadata for quote/RFQ files, tenant/doc/revision/owner, storage reference, size/type; use existing attachment storage service conventions. |
| `PoSourcingEvent` | append-only event/audit: created, quotes received/revised/withdrawn, compare, award, exception, approval, conversions, cancellations/reopen, actor/time/reason. Enforce immutability at `AppDbContext.SaveChanges*` (additive type registration alongside existing `ProcurementApprovalEvent` protections) and database/API rules; no cascade deletion of historic evidence. |

**Recommended normalization:** retain quote and awarded purchase quantities in their own UOM plus a locked conversion to canonical base quantity (decimal fixed precision); group/sum in base unit, with explicit round-trip tolerance using existing documented decimal precision. Do not infer ambiguous conversions. Keep monetary values as decimal with amount/currency/FX snapshot and rounding derived from existing PO/PR pricing policies for actual target documents. Quote comparison math is reporting, not posted valuation.

**Critical schema identity / integrity:** Each new child table must carry source parent scope or use database-enforced composite `(CompanyCode, BranchCode, ParentId)` keys so a branch/company cannot reference a source from another scope. A bare surrogate-id FK with service-side filtering alone is insufficient. Conversion source-to-target FK references must be validated against existing PR/PO schemas and revision keys; if direct relational FK cannot safely track optional target/history, use separately verified immutable reference with index and invariant tests, not a broken/cascading FK. Default ON/OFF settings do not create/drop data tables.

**Constraints/indexes:** FK chain among new tables with company/branch consistency; unique source document number per tenant/branch, unique source line number, unique quote revision per supplier/reference/source, unique conversion request id; required nonnegative qty/price, valid status, expiry / quotes; optimistic concurrency rowversion; indexes on source status, supplier, source PR line and target `(DocType, DocNo, RelNo, Line)`. Use protected schema names and verify FK definitions before execution; do not use float for monetary or quantity columns.

**Line identity / revision nuance:** PO native `UpdateAsync`/`ReviseAsync` may delete/recreate/resequence details, so target numeric `Line` alone is NOT a durable original-award identity. Preserve immutable source award line + conversion operation identity; append revision-specific target-line mapping records from prepared rows **in the same PO edit/revise transaction** or prove lineage reconstruction with exact unambiguous persisted keys. Reject ambiguous re-sequencing instead of linking unrelated items. A sourcing-first PR link must similarly survive PR changes. Never cascade-delete sourcing events when a target draft is legally deleted; record a terminal event and preserve its source lineage.

**Important transaction nuance:** `PoOrder` primary key is `(CompanyCode, BranchCode, PoNo, PoRelNo)` and is revisioned. A source-to-PO link must be revision-aware and not count old + latest revisions twice. `PoPr` is keyed `(CompanyCode, BranchCode, PrNo)`. `PoOrderDetail` stores PR link `(PrNo, PrLineNo)`. Link rows may be independent of existing PO schema; do **not** hijack `QuatationNo` as the only source linkage or treat it as a unique key.

**Deployment:** new `scripts/create-po-sourcing.sql`, **idempotent, manually DBA-run**, with `SET XACT_ABORT ON`, unique/index existence checks, safe rollout for multi-tenant databases, rollback instructions and forward-only evidence retention. Schema may be provisioned even for feature-OFF tenants without enabling functionality. No application-startup DDL. If a tenant has not deployed tables, feature OFF remains unaffected; enabling gives clear admin setup message rather than unhandled SQL error.

---

## 8. Server architecture and minimum integration touchpoints

**Add** (naming may follow repo conventions):
- `ErpWeb.Model/Entities/Purchase/PoSourcing*.cs` and `ErpWeb.Model/Configurations/Purchase/PoSourcing*Configuration.cs`.
- `ErpWeb.Core/Purchase/IPoSourcingService.cs`, `PoSourcingService.cs`, `PoSourcingComparisonCalculator.cs`, `PoSourcingConversionCoordinator.cs`, `IPurchaseSourcingFeatureGuard.cs` and attachment service only as needed.
- `ErpWeb.UI/Purchase/Transactions/PoSourcingList.razor(.cs)`, `PoSourcingWorkbench.razor(.cs/.css)` and optional inquiry page; use existing lookups and shared document UI.
- Add DbSets/mappings to `AppDbContext.cs`, scoped DI in `CoreServiceCollectionExtensions.cs`, `MenuCodes.PurchaseSourcing = "PO_SOURCING"` and menu entry under `PO_TRANSACTIONS` (e.g. `/purchase/sourcing`), menu permissions; add `AppSettingCatalogue` flag definition.
- `scripts/create-po-sourcing.sql`; targeted automated tests under `ErpWeb.Tests/Procurement/Sourcing/` and relevant existing Procurement tests.

**Change narrowly and only if required:**
- `PoPrService.SaveNewAsync` and `PoOrderService.SaveNewAsync` may take an **optional, verified source-conversion context** so they persist `PoSourcingConversion` rows **inside their existing SQL transactions before commit**, without altering their existing validations, calculations, numbering, document statuses, PR consumption or posting. Use a shared helper operating on the **same `AppDbContext`/transaction**, not a nested `SaveNew` with a second DB context/transaction. If a safe hook cannot be proved, stop and propose a reviewed alternative; **two independently committed writes are unacceptable**.
- Optional IDs in `PoPrSaveRequest` and `PoOrderSaveRequest` should be a server-resolved opaque conversion token or strongly verified award-line allocation set, **not** client-authoritative prices, supplier, quantities or approval state. User may edit an import draft, but server revalidates and stores final target document values and source mapping. **Source award != authority to bypass target validation**.
- `PoOrderService` already performs item lifecycle locking during preparation, then existing PR/approval/CJ locks during `LockConsumptionAsync`; PO revisions also acquire `PurchaseOrderRevisionSetLock` before that. **Do not take a sourcing-award lock first in an outer coordinator and then call the native save method**, because the native save itself takes other locks in the opposite order. Proposed native save-hook order for a sourced operation is: **PO revision lock where relevant → existing item lifecycle/PR/approval/CJ locks in their current proven order → sourcing award locks in ascending award IDs → check claim/setting authoritative row → target save + source event + commit**. Actual lock order including setting row must be validated against setting updates, award, revision and cancellation paths with a cycle table at Gate 0. If the existing order cannot be preserved safely, STOP Phase 4 rather than overriding PR consumption.
- **New conversion authority:** source operation receives server-signed/opaque request ID, but that ID is not permission. Inside the target transaction read/validate source award, selected quote revision, supplier, item identity, FX/UOM snapshot, remaining base qty, active company setting, permission, and target grouping; compare a stable input hash against any existing idempotency record. Same ID + same payload returns original target; same ID + different payload fails. Acquire transaction-owned SQL locks plus DB unique keys and reject `Remaining < 0`.
- **Target lifecycle:** source-aware, additive hooks on `PoPrService.UpdateAsync/CancelAsync/DeleteAsync` and `PoOrderService.UpdateAsync/ReviseAsync/CancelAsync/DeleteAsync/ForceCloseAsync/ReopenAsync` are necessary **only when a source link exists**, so native target mutations and source historical status cannot diverge. A derived, read-only status is preferable to metadata writes when sufficient. For a tenant that never installed the optional schema and feature OFF, the native PR/PO path MUST skip sourcing integration entirely (schema capability determined safely at startup/per-tenant readiness; never access absent tables). For a previously-sourced target, run source status maintenance even if feature OFF, **without denying native-valid actions**. If a native-valid edit removes/replaces the originally sourced identity while OFF, preserve immutable link, append a mismatch/detachment event, and **quarantine that award from further conversion after re-enable until reconciliation**; never erase its history or automatically free qty to purchase again. Test legal revisions/partial receipt/invoice with read-only downstream trace, no calls to cost posting.
- Prefer existing `ProcurementApprovalSqlLocks.TryAcquireAsync`/transaction-owned `sp_getapplock` pattern for award concurrency **only when its lock resource ordering is compatible**. Add unique DB constraints as final protection. Catch deadlocks/concurrency gracefully; no blind auto-retry with new request ID.
- Do **not** add a separate `SaveNewPOFromRFQ` implementation with hand-written SQL that bypasses `PoOrderService`. Do not open independent transactions for saving source links after PO commit.
- Ensure amount/quantity calculation uses existing `PoPrCalc`, `PoOrderCalc` and PO preparation; comparison snapshots only supply candidate values. Target services remain final validation authority.

### Conversion grouping and statuses
- Determine one target PO per `(Company, Branch, supplier, currency, required applicable header constraints)`; split if supplier differs. Warn buyer about splits and show a confirmation with resulting document count.
- Need appropriate `ADD` to PR/PO menu **and** sourcing source/award permission. Revalidate target vendor, purchasing unit, taxes, warehouse, project, item active status and permission on server.
- Target PR/PO starts in current `NEW` status and follows existing approval. A sourcing award sign-off is an independent buying decision only.
- No sourcing import may secretly merge lines with unlike source identities. In v1 preserve **one award allocation → one created target line** (unless a documented, verified native normalization performs a safe split). Map target lines from the **actual prepared/persisted** lines inside the transaction, never from a client row index. Persist snapshot of quantity base factor and source identifiers. If the target changes line order, reject the claim transaction instead of silently linking the wrong line.
- **No automatic update of preferred supplier or purchase price master.** Separate explicit enhancement later, with permission and audit, if requested.

---

## 9. Exact quantity and conversion safety rules

**State definition first:** Persist source *awarded allocation* separately from live target *effective quantity*. On an award-to-PR path, source commitment is created for the PR (the earliest linked target) and remains counted once when one or more POs consume it. An award-to-direct-PO path commits to the live PO. Exports and workbench summaries derive committed/released quantities from the **first-target source allocation plus its legal latest live state**; quote comparison never changes stock. A source-to-PR target must not silently free source commitment merely because the downstream PO has already consumed it. Source target legal cancellation/deletion releases the commitment **only** when permitted by existing target/downstream constraints and via an append-only release event.

1. **RFQ demand** is a planning quantity only; it must never reserve or increment/decrement stock, alter WIP, count as PO outstanding or affect availability queries.
2. `AwardedQty` per sourcing requirement line must be >= 0, and total awarded among active awards <= current authorized requested qty (same normalized base unit). Changes to requested qty after award require a reviewed new sourcing revision.
3. `ConvertibleQty` = awarded base qty minus **effective first-target source commitments**. Explicitly define `Commitment(PR)` as live source-linked PR quantity **counted once**, including its PO descendants, and `Commitment(DirectPO)` as the active latest PO-revision quantity. Never add the derived PO quantity to its originating PR allocation. The source layer must not reinterpret PR usage to change native PO available quantity. Define one canonical query, not duplicated page math. For direct PO, commitments count only latest live revision with valid target status. For PR path, active PR source allocations count once, even when the PR later produces one or more POs; do not also count the PR-linked POs as new sourcing allocations.
4. `QuoteQty` may exceed demand; `AwardQty` may not exceed demand without explicit RFQ demand revision/reapproval. Partial awards and partial conversions are supported, no silent rounding-up to full pack size. Honor MOQ/pack increments and explain incompatibility.
5. A cancelled/unissued target can release its remaining source commitment **only** after confirming target cancellation is legal under existing PO/PR receipt/invoice/downstream rules. Preserve the original conversion event and record a release event. Never release source qty for a document still received, invoiced or otherwise active.
6. PO revision replaces the *effective* commitment with the latest revision, not cumulative all revisions. If revised quantity increases, recheck remaining award; if decreases, compute released quantity correctly. Do not modify existing PO revision rules or allow revision to bypass an approved source award without an auditable exception path.
7. If source was PR-first, existing PO validation still applies: `PoPrCalc.IsAvailableForPo`, approval and remaining PR qty, `LockConsumptionAsync`, `ValidateAndApplyPrConsumptionAsync` and `StampPrConsumptionAsync`. Source quantity checking is an **additional** constraint, not replacement.
8. Duplicate-click, browser retry, concurrent sessions or request timeout with unknown outcome must use **per-target-group persisted** idempotency request key and immutable canonical input payload hash; subsequent same request returns original PR/PO result, not a second document. Different request keys contend safely for the same awarded balance. No partial success presented as success without clear per-document status.
9. Multi-PO generation is **not** atomic if it calls multiple independent PO saves. Choose an explicit policy: **v1 safest:** generate one target at a time with separate idempotency, persist target progress, and show partial conversion if an individual PO fails. Never promise all-or-none across vendors unless a verified shared multi-document transaction implementation exists. **All-or-none must apply to each individual PO/PR + its source-link record.** A failed later supplier does not roll back valid earlier PO; buyer sees exact outstanding allocations.
10. Source qty/UOM normalization, source map identities and award locks must have SQL Server concurrency tests with two simultaneous buyers. **No negative remaining awarded quantity.**
11. **No dual-path quantity claims.** An award line cannot be converted both to a PR and a direct PO beyond its effective award amount. A source-first PR later used for PO retains the PR-path claim; direct PO conversion can only use genuinely uncommitted award remainder. Store/validate the conversion path per allocation, never double count.
12. **Multiple same-item lines:** use stable RFQ line IDs, quote line IDs, award allocation IDs and source target-line mapping. No grouping solely by item code; identical item/warehouse/UOM on distinct requirement lines must remain traceable individually.
13. **UOM:** compute award quantity in canonical source base UOM using verified item/UOM and pack factor; reject any source factor that does not match native target prepared `StdQty/PoPurQty` within existing quantity precision. Never change `PoPrCalc.RoundQty`, `PoOrderCalc.RoundQty`, `PoOrderCalc.ComputeStdQty` or persisted stock standard-UOM quantities.
14. **Native PO grouping:** must split by supplier, currency, tax-inclusive flag, one-time/stock mode and other validated header constraints. Do not let a group fail just because it silently mixed unsupported line modes. Quote-only supplier and quote-only free-text item are valid for comparison but not unconditionally convertible to native PO.
15. **Sourced target identity and disable-safe operations:** while the feature is **ON**, reject supplier/item/UOM changes that invalidate an awarded mapping and guide the buyer to a legal cancel/re-award/recreate path; allow permitted quantity/price edits only after current native target permission, quantity/approval and award-balance checks. While feature is **OFF**, NEVER deny a native-valid PR/PO edit solely because of sourcing; still retain source lineage and append an auditable **source mismatch/detachment** event if native-valid changes break the original award identity. Such source allocation is **not silently released for re-award**; on re-enable, quarantine the affected award from new conversion until a permissioned reconciliation determines effective commitments. No downstream cost/stock edits.

---

## 10. Pricing, tax and costing boundaries

| Area | Allowed change | Forbidden change |
|---|---|---|
| Source price comparison | Read-only calculations from supplier quote snapshots; compare based on explicit verified currency, pack/UOM, discounts, freight and stated tax rules. | Alter inventory valuation, GRN costing, PI costing, master stock cost or purchase cost adjustment. |
| RFQ/quote/award | No inventory or GL posting; audit only. | Write `IvBalLoc`, `IvTrxHistory`, stock postings, FIFO layers, WIP or account entries. |
| PR/PO creation | Reuse existing PR/PO tax and qty validation; source values are input proposals only. | Fork existing costing/quantity logic or bypass PR/PO approval. |
| Downstream GRN/PI | Continue current normal behavior; link source to existing PO/PR for read-only trace. | Alter GRN posting, rollback, invoice settlement, cost-layer generation, closed period or future-date gates. |
| Historical prices | Display reference price with source/date and non-comparable warning if needed. | Overwrite actual purchasing history or change its FX/UOM semantic silently. |

**Data-quality gates:** nonnegative prices/qty, positive safe UOM conversions, explicit precision and rounding, currency + exchange rate date required for cross-currency comparison, tax-inclusive/exclusive recognized, quote expiry and MOQ flagged, source-price changes from PO draft visible before save. **Proof that the target will retain the expected price is mandatory:** `PoOrderService.PrepareLinesAsync` falls back to a master/vendor unit price on new lines with `PoUnitPrice=0`; a zero quote must never auto-convert into a priced PO unnoticed. Other nonzero sourced unit prices may be rounded by `_options.POPriceDecimal`; preflight the resulting value, compare against award snapshots and require an auditable exception or reject. The existing target `PoOrderCalc` remains unchanged. Permit a buyer to negotiate PO price different from award **only with explicit confirmation, reason, and target-document permission/policy**, while preserving original quoted and awarded snapshots. Do not silently overwrite either.

**Costing explicitly out of scope:** Even if the buyer labels freight as "landed cost", comparison amounts are **estimates only**. Source freight, insurance and tax allocations must NOT be written to `PurchaseReceiptCostSettlement`, `PurchaseCostAdjustment`, `IvBalLoc`, `StockFifoLayer`, `CostLayer`, GL/stock postings or source-of-truth actual invoice amounts. Supplier quote retention does not create commitments in GL. Actual downstream purchase valuation is unchanged.

**Closed/future periods:** RFQ and quote dates are informational; no inventory posting so no stock period impact. PR/PO date validation follows existing document rules. Never change future-date posting or month-end controls downstream.

---

## 11. Permission and audit matrix

| Action | Sourcing permission | Additional mandatory control |
|---|---|---|
| See sourcing list/detail | `PO_SOURCING:ACCESS` | Feature enabled; company/branch scoped. |
| Create/duplicate sourcing case | `ADD` | Feature enabled; valid company/branch and source. |
| Edit draft, enter quote, upload | `EDIT` | Correct status/ownership per current pattern; not frozen/approved; secure attachment. |
| Cancel draft/case | `CANCEL` | Downstream commitments checked; record reason. |
| Delete draft | `DELETE` | Only truly unused draft with no quote/award/reference; otherwise cancel. |
| Submit for comparison / award decision | `SUBMIT` | Complete normalized comparison data. |
| Final award / exception approval | `APPROVE` | Immutable snapshot, reason for override, conflict-free allocations. |
| Reject award for rework | `REJECT` | Reason; create revised candidate; do not mutate past award snapshot. |
| Create PR | Sourcing `ACCESS` + appropriate conversion permission (e.g. `SUBMIT`) | **Also** `PO_PR:ADD`, existing target validation. |
| Create PO | Sourcing `ACCESS` + appropriate conversion permission (e.g. `SUBMIT`) | **Also** `PO_ORDER:ADD`, existing direct-PO and approval policies. |
| Show negotiated price / export cost | `VIEW_COST` | Enforced in service DTO and exports, not only browser. |
| Export analysis | `EXPORT` | Tenant filtering and price visibility. |

For SME usability in v1, an authorized buyer may sign off their own award **only if the account explicitly holds `APPROVE`**; record creator and approver even if identical. Do not claim separation-of-duties or create a configurable multi-step policy in this version. A later optional separation-of-duties policy requires a separate approval-scoped enhancement. Administrative role bypass may bypass menu permission by existing design, **never** the company OFF flag or tenant scope. When adding menu codes, configure permission possibilities and default grants explicitly; `MenuSyncService` currently ensures ACCESS pairs but does not automatically mean every role gets ADD/EDIT/APPROVE. Test operator, buyer, approver, finance and read-only personas.

Audit immutable events with `CompanyCode`, `BranchCode`, source/quote/award/target IDs, actor, UTC timestamp, old/new status, reason, relevant snapshot hash/version. Audit denied conversions or high-risk overrides in existing logging pattern; do not leak private quotes across tenants. Avoid hard deletion of awarded/converted records.

---

## 12. Lifecycle and state machine (do not use POSTED for sourcing)

**Sourcing case:** `DRAFT → COLLECTING_QUOTES → READY_TO_COMPARE → AWAITING_AWARD → AWARDED → PARTIALLY_CONVERTED → CONVERTED` where last two are preferably *derived* from live document allocations. `CANCELLED` only by guarded cancellation. “EXPIRED” denotes deadline/quote validity, not an automatic destructive transition.

**Quote:** `DRAFT → RECEIVED → REVISED/SUPERSEDED | WITHDRAWN | DECLINED | EXPIRED`. Original accepted quote revision remains immutable after award.

**Award:** `PROPOSED → APPROVED | REJECTED`; if amended, supersede with an explicit linked revision. Never edit approved award lines in place.

**PR/PO:** existing statuses unchanged. Source state and link state derived from PR/PO live statuses; do not treat submitted/approved sourcing as submitted/approved PO.

**Lifecycle transition contract:** `Create/Edit/Cancel/Delete` source case permitted only in eligible native source state; `QuoteReceived/Revised/Withdrawn` append a version/event; `AwardApprove/Reject/Supersede` append immutable approval evidence. `CreateTarget` is separately authorized and transactionally claimed. A quote expiry after an approved award triggers a warning/authorized exception at conversion, **never** retroactively cancels already created PR/PO. Source cancel with outstanding active conversion is denied. Read-only derived source progress must never update GL/stock.

**Cancellation/removal rules:** reject removal if dependent quotes/awards/conversions exist; cancel or supersede instead. Handle quote withdrawal after award by raising a warning and requiring explicit re-award decision, not altering an already-created PO. Preserve lineage on target PO revision, cancel, force close, receipt and invoice.

---

## 13. Phase-by-phase coding agent execution plan

### Gate 0 — Repository evidence and safety baseline (COMPLETED on `960cbeb1a388af50589e095c3caa461d5c6502e0`)
1. **Verified HEAD & Worktree:** `960cbeb1a388af50589e095c3caa461d5c6502e0`, clean baseline build (`dotnet build ErpWeb.Tests/ErpWeb.Tests.csproj -m:1 -nodeReuse:false -p:UseSharedCompilation=false` succeeded with 0 errors).
2. **Gate 0 Evidence Table:**
   - `ErpWeb.Core/Purchase/PoPrService.cs:1943-1961`: `PrepareLinesAsync` ignores `src.UnitPrice` on new lines; V2.1 adds a scoped sourcelink-aware check to preserve `RoundPrice(src.UnitPrice)` when `src.UnitPrice > 0m` under a verified sourcing conversion context.
   - `ErpWeb.Core/Purchase/PoPrService.cs:2053-2080`: `EnforceCurrencyInclusive` requires uniform `Currency` and `IsInclusive` across all lines in a PR; Create PR splits by `(CompanyCode, BranchCode, Currency, IsInclusive, PrType)`.
   - `ErpWeb.Core/Purchase/PoOrderService.cs:582-747` & `1669-1897`: `SaveNewAsync` lacks server-side `PoSupplier` existence check; `PrepareLinesAsync` falls back to catalog price when `PoUnitPrice == 0`; `EnforceDocumentMode` splits by `IsInclusive` and `OneTimeItemYn`; `SynchronizeDetails` deletes and re-inserts all details on every `UpdateAsync`.
   - `ErpWeb.Core/Settings/AppSettingService.cs:296-385` & `AdSmParamConfiguration.cs:19-51`: `CompanyCode`/`BranchCode` are `nvarchar(5)`; `ClearAsync` deletes the `AdSmParam` row.
3. **Transaction-Lock Ordering Graph (No-Cycle Invariant):**
   1. `AdSmParam` company setting row lock (`UPDLOCK, HOLDLOCK` on `(ModuleCode = N'PROCUREMENT', ParamKey = N'SOURCING_ENABLED', ScopeCode = N'COMPANY', CompanyCode, BranchCode = N'')`) — acquired FIRST on any sourcing write or sourced conversion.
   2. `PoOrderRevisionSetLock` (`sp_getapplock` on `PO_ORDER_REV:{company}:{branch}:{poNo}`) — when revising/updating a PO.
   3. Item lifecycle / PR consumption / Procurement approval locks (`ItemLifecyclePolicy`, `PoPrService.LockPrAsync`, `ProcurementApprovalSqlLocks`) in their native order.
   4. `PoSourcingAward` / `PoSourcingHdr` locks in ascending `SourcingId` / `AwardId` order before persisting `PoSourcingConversion` / `PoSourcingConversionLine` inside the same `AppDbContext` transaction.

**Exit criteria:** Gate 0 Static & Build Baseline **PASS**. Proceed to Phase 1.

### Phase 1 — Company switch, schema, permissions and skeleton
1. Add catalogue flag OFF; tests default, company isolation, invalid stored value fail-closed, toggle concurrency. Verify SQL administrative ON/OFF transaction serialization with sourced writes; cached settings cannot authorize a write.
2. Add new sourcing tables/configurations and idempotent DBA script; clean upgrade/rollback documentation. Only `PoSourcing*` tables, no core inventory/financial schema changes.
3. Add scoped service interfaces/feature guard, `MenuCodes.PurchaseSourcing`, XML menu and per-company navigation filter; service/route/auth/export guards.
4. Add empty/list page states for enabled company; no PR/PO hooks yet.

**Exit criteria:** disabled company's old Procurement unchanged; direct-source URL denied; company A ON/company B OFF passes; script reruns safely; all old tests green.

### Phase 2 — RFQ and quote entry
1. Create from blank or eligible remaining PR line; reuse supplier/item pickers and UOM/warehouse/project lookups.
2. Add suppliers including quotation-only candidates; quote revision and optional attached files; responsive DevExpress Request/Quotes panes.
3. Add read-only source inquiry, strict statuses, edit/cancel/attachments permissions, audited revision and secure download. Prevent users without source quote price permission from opening **unredactable attached originals**.

**Exit criteria:** buyers can record 3 competing offers without creating a vendor master or PO; revisions and uploads remain scoped; no stock/cost tables written.

### Phase 3 — Comparison and award
1. Implement pure comparison calculator, validated rate/UOM snapshots, comparable totals and incomplete/noncomparable warnings.
2. Build comparison workbench with split awards, exception reason, award sign-off and immutable snapshots.
3. Lock/guard concurrency for awarded quantities and provide audit. Add read-only price-history context without changing old history.

**Exit criteria:** same-currency, cross-currency, different pack sizes, inclusive/exclusive tax, freight, invalid FX/UOM, expired quote, partial quote, over-allocation and concurrent award tests pass.

### Phase 4 — Safe PR/PO conversions (HIGH RISK; explicit gate)
1. Add optional source context to existing PR/PO save requests/services **only after Gate 0 validates exact hook placement and lock order**. Existing services create their own contexts/transactions. A coordinator using public `SaveNewAsync` followed by source-link insert is PROHIBITED. A native internal hook sharing target `db`/transaction is mandatory; avoid broad code extraction or a parallel save path. Use a shared SQL transaction for target and source link. Do not change tax/qty, numbering, PR consumption or approval math.
2. Implement award→PR (NEW) and direct award→PO (NEW) as server-verified drafts using current service validators; preserve supplier/currency grouping and normalized qty validation.
3. Implement PR-first source→PO with precise vendor-blank PR handling, approved PR visibility and existing `LockConsumptionAsync`/approval/remaining-qty checks. Re-check current PR remaining quantity inside the final PO transaction because ordinary PO users can consume PR quantity while sourcing is in progress. Do not loosen unrelated PR picker behavior.
4. Add **per-target-group request id + payload hash** idempotency, conversion allocations, latest-revision-aware source-link update/reconciliation, partial multi-vendor progress, safe cancel/revise/reopen trace. Add source-linked handlers for **PR Update/Cancel/Delete** and **PO Update/Revise/Cancel/Delete/ForceClose/Reopen** only where the source link exists. Preserve old path for non-sourced targets and feature-OFF companies.
5. Add SQL Server multi-user concurrency tests and regression tests for PR/PO New→Edit→Approval→PO revision→GRN→PI→legal Cancel/Rollback paths. Include simultaneous disable vs conversion, sourced target edit vs another source conversion, source award vs re-award, and **same idempotency key/different payload** conflict. **No downstream business logic edits.**

**Exit criteria:** no duplicate PO/PR after retry; concurrent users cannot overconsume award or PR; each PR/PO save and source link commit/rollback together; existing PR/PO tests unchanged; no cost/stock regression.

### Phase 5 — Inquiry, UAT, deployment and operational handover
1. Add source-to-PR/PO/GRN/PI trace, unread/expired quotations, vendor comparison and export, server-side VIEW_COST filtering.
2. Responsive UI polish; prove compact daily entry; test tablet and desktop browsers, keyboard navigation, all error states, permissions and disabled company.
3. Run full automated test suite and SQL Server concurrent-integration/UAT scripts with baseline data snapshots of stock/cost/accounting. Confirm zero changes from RFQ/award alone.
4. Prepare deployment package: DBA script; feature OFF rollout; admin enable instructions; support troubleshooting and rollback-by-disable; release notes; screenshots and sample UAT dataset.

**Exit criteria:** no unresolved Critical/High defects, signed test matrix, independently reproducible scripts and deployment plan; company OFF and ON both verified. **Not “approved 10/10” until evidence supports it.**

---

## 14. Required automated tests / UAT matrix

### Feature OFF / tenancy / security
- Default OFF with no stored row; invalid/unrecognized setting OFF; missing sourcing tables while OFF does not break existing PR/PO.
- Company A ON/company B OFF in same DB; separate tenant DBs; branch A cannot open branch B quotation (including attachments, export, direct URL).
- User opens workbench then admin disables before save; save rejected safely; no orphan source documents/PO links. Re-enable restores prior records.
- Buyer has sourcing ACCESS but lacks ADD/EDIT/APPROVE; direct HTTP calls blocked. ADMIN permission bypass does not bypass OFF.
- VIEW_COST denied → price fields not present in API DTO, downloads/exports, print or report data; **source quote attachments whose contents cannot be selectively redacted are forbidden to download**, not merely hidden on screen.

### RFQ, quotation, comparison
- Blank source RFQ; from PR with eligible quantities; PR cancelled/approved/pending; PR with blank vendor; service item/one-time item; inactive item.
- 0 quotes, 1 quote, 3 quotes; supplier quote only some items; supplier declines; expired/withdrawn; multiple revisions and attachment retained.
- Quoted pack=10 vs base unit piece; factor missing/mismatched; fractional qty, rounding and MOQ; quote rate snapshot absent and currency mismatch.
- Inclusive vs exclusive tax; discount, header freight, nonrecoverable charges, recoverable taxes, tax rounding; compare best **equivalent total**; past invoice-price reference not mistaken for matched price.
- Cheaper supplier has slower lead time; override non-lowest award with required reason and permission.
- Split 60/40; multiple lines with same item; overaward blocked; quote expiry mid-award; double-click award; concurrent same-line awards.

### PR/PO conversion and downstream
- Direct award→PO (no PR), allowed vendor, invalid vendor, quote-only supplier, unsupported currency/UOM, expired quote, PO approval pending. **Explicit zero-price quote** cannot silently fall back to vendor/master price; source-only free-text item cannot silently become an invalid PO. Confirm PO grouping splits inclusive/exclusive and one-time/stock lines into separate legal documents.
- Award→PR then PR approval→PO, source PR with blank vendor, selected vendor differs from PR, PR fully/partially ordered, PR remaining qty contention.
- 3 suppliers → 3 POs; one PO fails → others persist with correct source remaining and honest UI; retry failed target does not duplicate successful POs.
- Source→PR/PO same request twice, **same key/different payload**, two users simultaneously, network timeout after commit, deadlock retry, numbering error, validation exception, attachment failure. SQL Server two-session **admin disables feature while conversion is committing**. Confirm strict linearization and no orphan source/target docs. Assert no orphans and deterministic recovery.
- PO revision increase/decrease, **line resequencing/line removal/identical item on multiple lines**, **supplier change**, **tax mode change**, cancelled PO with no downstream, legally noncancellable received/invoiced PO, partial GRN, PO close/reopen, invoice with and without PO; latest revision only counted once.
- PR cancellation, PR deletion, PR revision/edit quantity/vendor/UOM, PR approval withdrawal, changed target price, one-time supplier policy and company OFF after conversion. Explicitly verify normal PR/PO update remains usable when feature OFF but source-linked historical trace still reconciles. Sourcing sign-off must never approve PR/PO automatically.
- Verify existing GRN posting/rollback, PI settlement, stock quantity/ledger and cost-layer valuation remain byte-for-byte/equivalent financially under test fixtures (allow known nondeterministic audit timestamps only).
- Verify original non-sourcing PR/PO behavior, normal PR vendor picker, direct PO creation, approval inbox, document relationships, purchase inquiry and price-history views unchanged.

### Performance / UX / accessibility
- 100+ RFQ lines, 10 suppliers, 2+ revisions, 1000+ historical RFQs; use server-side paging/filtering, avoid O(n²) database round trips and load only needed comparison matrix.
- Compare grid readable at desktop 1366px and tablet widths; no hidden action buttons at extreme right; lookups searchable; paste/keyboard entry; Save/Cancel behavior consistent with existing PR/PO; no confusing technical text.
- Database queries bounded by `(CompanyCode, BranchCode)`; use appropriate covering indexes and query plans for outstanding/source-link analysis.

---

## 15. Acceptance criteria (business sign-off)

1. For every company not opting in, **all existing Procurement behavior, menus and test results remain equivalent**, without extra setup or extra clicks.
2. Company admin can enable/disable the feature in existing Procurement settings; default is OFF and switching OFF never deletes historical data or disables existing PR/PO.
3. Buyer can record and compare at least three supplier quotes (including a non-master supplier candidate), view source attachment and price-history reference, and understand why offers differ.
4. Comparison is mathematically comparable across supported UOM/currency/tax/freight; incomplete offers are clearly flagged, not misleadingly ranked.
5. Buyer can award per item/partial quantity, can override cheapest with reason, and authorized approver finalizes decisions with immutable history.
6. Authorized user can create **PR or PO** without retyping; target service retains all current permissions, validation, quantity and approval controls.
7. PR-first and source-first paths both work; vendor-blank PR handled intentionally without regressing current vendor filtering.
8. Concurrency and retries cannot over-allocate or duplicate source conversions; each target-document creation commits atomically with its source link and canonical request hash. **Test source-to-PR and source-to-direct-PO are mutually accounted for**, including PR then downstream PO and PO revisions.
9. Source-to-PR/PO/GRN/PI trace shows correct latest revision, status and converted/remaining quantity; target PR edit/cancel/delete and target PO edit/revise/cancel/delete/force-close/reopen do not silently corrupt sourcing balances; cancelled/revised links not double counted.
10. No changes to FIFO/WAC/CostLayer, valuation, `IvBalLoc`, posting/reversal, period close or GRN/PI costing; existing regression tests continue passing.
11. Role/tenant/branch/attachment/export controls are enforced on server; disabled company blocked even if user guesses the URL.
12. DevExpress workbench is professional, responsive, quick for daily use, with sensible widths, action buttons at left and standard Save/Cancel.

---

## 16. Agent MUST NOT change

- **DO NOT** rewrite existing `PoPrCalc`, `PoOrderCalc`, PR/PO tax/qty conversion, PR consumption or direct PO behavior just to make sourcing easier. **DO NOT** alter PO new-line zero-price fallback or mixed-mode restrictions globally; block unsupported sourcing conversion or raise a separate tightly scoped change request requiring explicit approval.
- **DO NOT** change `IvInventoryPostingService*`, `StockCost*`, `CostLayer`, `IvBalLoc`, stock movement, FIFO/WAC/standard costing or inventory/production posting/reversal code. If a defect is discovered there, report separately; do not silently fix under this feature.
- **DO NOT** change GRN, purchase invoice posting/settlement, cost adjustment, closed period, future transaction or negative-stock rules.
- **DO NOT** bypass existing PR/PO approval gate, modify approval policy/fingerprint semantics, auto-approve PR/PO or globally relax PR vendor filters. Source-only PR vendor blank handling must not admit a conflicting explicit PR supplier.
- **DO NOT** automatically update supplier master, per-item vendor price, stock standard cost, inventory buying price or purchase history based on an RFQ quote.
- **DO NOT** introduce a new pricing engine, a second transaction/save pipeline, a new tenant-config database, a generic workflow engine or new JavaScript framework.
- **DO NOT** treat UI hiding as an authorization check, rely on client-supplied award quantities/price/permissions, store untrusted file paths, or leak pricing in exports.
- **DO NOT** deploy auto-DDL on startup, delete historical sourcing data on disable, or blindly backfill/source-link legacy POs.
- **DO NOT** mark the implementation approved just because tests were skipped or only a happy path was tested.

## 16A. Required hard gates and stop criteria (non-negotiable)

**Gate A — Optional feature isolation:** OFF before schema install and OFF after prior ON; existing PR/PO/GRN/Invoice remain functional, no default leakage or extra clicks. Approval controls unaffected. If ON/OFF is only cached and not proven transaction-safe, FAIL.

**Gate B — Pricing identity:** quote-to-target preview shows native validated quantity, tax mode, buying/party constraints and exact effective unit price. If zero-price quote would silently adopt master unit price, **BLOCK that conversion**. Never "fix" PO general pricing automatically.

**Gate C — Source target transaction:** evidence must show one SQL transaction owns award claim, idempotency record, target header/lines/numbering, and source link. A caller wrapping `PoPrService.SaveNewAsync` / `PoOrderService.SaveNewAsync` in a separate unit of work is **not atomic** and FAILS. Confirm rollback on failure at each save boundary and timeout-after-commit recovery.

**Gate D — Concurrency graph:** prove stable lock acquisition order across **normal PO actions and sourced actions**, including PR row locks, PO revision lock, item lifecycle locks, sourcing award locks, disable flag DB row and database unique keys. Parallel SQL Server tests must demonstrate no duplicate/overcommitted award under mixed workflows, including concurrent source award vs conversion vs PO revision vs cancel.

**Gate E — Source-linked editing:** when optional schema absent and feature OFF, standard PR/PO must operate without sourcing-table access. When source link exists and feature ON, source-linked target mutations must update historical link/derived effective commitment atomically or reject unsafe mutation. When feature OFF, native-valid PR/PO actions MUST NOT be blocked by historical sourcing link; record changed-source mismatch and hold further sourcing conversion on re-enable until audited reconciliation. If no stable line-mapping contract across PO detail resequencing exists, FAIL. Existing downstream GRN/PI quantity/cost logic remains exactly unchanged.

**Gate F — Approval/security:** source award APPROVE is neither PR nor PO approval. All writes check tenant/branch, feature flag and permissions on server; VIEW_COST redaction applies across all outputs and attachments/report metadata where sensitive. No allowed path turns a non-master quote supplier or free-text item into a valid PO without native validation.

**Gate G — Deployment:** idempotent schema script on SQL Server UAT database, pre-deployment backup plan, feature OFF rollout, staged enablement, disable-as-recovery, no destructive rollback of business audit data. No unmeasured performance regressions and no unresolved Critical/High UAT defect.

**Mandatory agent response when a hard gate fails:** stop implementation of the unsafe phase, preserve already-working code, document root cause and exact file lines, propose a minimal safe alternate plan, and request explicit approval if an existing calculation, posting or costing rule would have to change. **Never mark APPROVED FOR IMPLEMENTATION — 10/10 from document-only review.**


---

## 17. Work product requirements for the coding agent

At end of **each phase**, deliver: current commit + changed file list; precise business logic summary; risk checklist for feature OFF, permissions, quantities, PR/PO approval, UOM/FX and idempotency; test command/result/log; screenshots for relevant UI; any known limitation; Gate PASS/FAIL decision. If a critical issue is found, **stop at the gate** and provide a proposed minimal remediation rather than silently altering core business logic.

**Deployment recommendation:** install additive schema with feature OFF; enable for one internal/pilot company only after production-like SQL Server UAT; then opt in other companies deliberately. Keep a per-company switch-off path as operational rollback—**switching off does not reverse any already-created PR or PO**.

**Final sign-off required:** full behavior comparison OFF vs pre-feature, approved business flows ON, matched source quantities under concurrent load, and demonstrable zero impact on posted stock/valuation.

---

## 18. Source links (verified branch)

- Settings catalogue: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Settings/AppSettingCatalogue.cs
- PR service: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Purchase/PoPrService.cs
- PO service: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Purchase/PoOrderService.cs
- PO detail entity: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Model/Entities/Purchase/PoOrderDetail.cs
- Supplier-by-item entity: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Model/Entities/Purchase/PoVendorByItem.cs
- Menu XML: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb/Menus/menus.xml
- Navigation service: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Menus/NavigationService.cs
- Procurement approval service: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Purchase/ProcurementApprovalService.cs
- Price history UI: https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.UI/Purchase/Inquiry/PoPriceHistoryInquiry.razor.cs
- Existing SQL script style: https://github.com/mokth/net10projectTemplate/blob/productionv2/scripts/create-po-approval.sql

**Plan end — V2.1 GATE 0 VERIFIED. STATUS: APPROVED FOR PHASED IMPLEMENTATION UNDER HARD GATES A–G.**
