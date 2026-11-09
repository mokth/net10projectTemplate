# QA Safety Fix — F4 professional Quality UI

**Gate:** F4 only.  
**Status:** IMPLEMENTED — STOP before F5.  
**Recorded:** 2026-11-04, local Windows checkout.  
**HEAD:** `dcb614e` (`fix/dataprotection-key-ring`).  
**Class:** A UI-only. Zero costing, quantity, WIP, ledger, schema, or SQL changes. No flag enablement. No live rollout.

## 1. What F4 did

Record-only copy (R-24) is now the operator-facing contract:

> Inspection record only — posting approval not active.

- Settings no longer offers checkboxes for Incoming, Finished-goods, Non-good, Logical hold, or Physical hold. Those rows are read-only Off/On with “Not available yet” (or “Persisted outside Settings” if already ON).
- Settings Save still sends the persisted frozen-gate values and only edits inspector-approver separation.
- Workbench, Case, Inquiry, and Settings show the shared record-only banner and a How to use popup.
- Request QA on GRN, miscellaneous receipt, Daily Production, and finished-good receipt is presentation-only: the same navigation, plus the record-only title.
- Inquiry uses DxGrid on desktop and compact cards on small screens. Case lines get a compact card layout.

Allowlist touched: `ErpWeb.UI/Quality/*` and Request QA markup on the four source documents. Dirty Settings files were redesigned in this gate, not overwritten blindly.

## 2. Files

Added: `QaRecordOnlyBanner.razor(+css)`, `QaHowToUsePopup.razor(+css)`, this evidence file.  
Edited: Settings, Workbench, Case, Inquiry; Request QA titles on GRN / MR / Daily Production / FG.  
Not edited: Core, Model, SQL scripts, posting math, policy flags, plan file.

## 3. Verification

```
dotnet build ErpWeb.UI/ErpWeb.UI.csproj --no-restore
Build succeeded. 0 errors.
```

Local `http://localhost:5000/quality/settings` redirected to Sign in. Visual matrix at 1440 / 1024 / 768 / 390, 100% / 125%, light / dark: **NOT RUN** (login wall; no credentials used).

SQL Server UAT / live flag enablement: **NOT AUTHORIZED**.

## 4. GO / NO-GO

| Gate | Result |
|------|--------|
| F4 professional record-only UI | **PASS** as local compile evidence |
| Browser visual matrix | **NOT RUN** |
| Flag enablement / live SQL / F5 | **NO-GO** |

**STOP before F5.**
