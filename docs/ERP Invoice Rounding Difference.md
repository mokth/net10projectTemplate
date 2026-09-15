# ERP Invoice Rounding Difference – Implementation Plan

## Objective

Fix common RM0.01–RM0.02 differences between the ERP Sales Invoice and the Accounting system.

Call this:

**Rounding Difference / Rounding Adjustment**

Do not modify the original invoice price or tax just to force the accounting posting to balance.

---

## 1. Inspect Existing Code First

Before changing anything, trace the existing flow:

```text
Sales Invoice
 → Line Calculation
 → Discount
 → Tax Calculation
 → Grand Total
 → Accounting GL Posting
 → Debit/Credit Validation
```

Search for all existing `Math.Round()`, tax rounding, total calculation and GL posting logic.

Do not create duplicate rounding logic.

---

## 2. Centralize Rounding

Use `decimal` for all monetary calculations.

Create a shared:

```csharp
IRoundingService
```

with a consistent currency rounding rule, normally **2 decimal places**.

Example:

```csharp
decimal RoundMoney(decimal value)
```

Use the same rounding policy throughout Invoice, Tax and Accounting posting.

Do not use `double` or `float`.

---

## 3. Avoid Excessive Intermediate Rounding

Do not round every calculation unnecessarily.

Prefer:

```text
Calculate with precision
 → Calculate tax
 → Calculate invoice total
 → Final currency rounding
 → Build GL
```

The rounding policy must match the existing Accounting system wherever possible.

---

## 4. Handle GL Rounding Difference

After building the accounting journal:

```csharp
difference = RoundMoney(totalDebit - totalCredit);
```

If:

```text
difference == 0.00
```

→ Post normally.

If:

```text
difference = ±0.01 / ±0.02
```

→ Create a **Rounding Adjustment** so the journal balances.

Example:

```text
Invoice Total       RM100.01
GL calculated       RM100.00
Rounding Adjustment   RM0.01
---------------------------
Posted Total        RM100.01
```

---

## 5. Rounding Gain / Loss Accounts

Do not hard-code the GL account.

Create configurable:

```text
Rounding Gain Account
Rounding Loss Account
Maximum Rounding Tolerance
```

Recommended default tolerance:

```text
RM0.02
```

The exact tolerance should be configurable.

---

## 6. Reject Real Errors

Do NOT use rounding adjustment to hide real calculation problems.

For example:

```text
RM0.01 → rounding
RM0.02 → rounding
RM0.05 → investigate/reject
RM1.00 → reject
```

If difference exceeds tolerance:

```csharp
throw new InvoicePostingException(
    "Accounting posting difference exceeds rounding tolerance.");
```

---

## 7. Keep Blazor UI Thin

Do not put rounding/accounting logic inside `.razor` pages.

Use:

```csharp
await _invoicePostingService.PostAsync(invoiceId);
```

Business logic belongs in the application/service layer.

---

## 8. Journal Must Always Balance

Before posting:

```text
Debit == Credit
```

After applying rounding adjustment, validate again.

If still unbalanced:

```text
STOP POSTING
```

Never post an unbalanced journal.

---

## 9. Audit / Logging

When a rounding adjustment occurs, record:

```text
Invoice No
Original/Expected Amount
Actual GL Amount
Rounding Difference
Rounding Account
Adjustment Amount
```

Log it for reconciliation and troubleshooting.

---

## 10. Test Cases

Code Agent must test:

- No difference
- +RM0.01
- -RM0.01
- +RM0.02
- -RM0.02
- Difference > tolerance
- Multi-line invoice
- Discount
- Tax
- Tax Inclusive
- Tax Exclusive
- Sales Invoice
- Credit Note
- Debit Note
- Final Debit = Credit

---

## Acceptance Criteria

The implementation is complete when:

1. Existing invoice calculations remain correct.
2. RM0.01/RM0.02 legitimate differences are automatically handled.
3. Accounting journals always balance.
4. Rounding Gain/Loss accounts are configurable.
5. Differences above tolerance are rejected.
6. Rounding logic is centralized and reusable.
7. Blazor UI contains no accounting calculation logic.
8. Rounding adjustments are auditable.
9. Existing posted transactions are not changed.

### Core Rule

**Do not change the invoice to fix a rounding problem.  
Add an explicit Rounding Adjustment to make the accounting journal balance.**