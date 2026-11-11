# Sales Pricing Phase A — Browser UAT (11 November 2026)

**Decision:** **PARTIAL.** The screens that this DEMO tenant can exercise passed. DO commercial copy and CN amount2 warning were **not** live-proven because the tenant has no posted DOs and no posted invoice with a second amount discount.

**Class:** B costing-adjacent. No Post, no Rollback, no stock/cost mutation. SO line qty change was discarded in the popup (database still `OrderQty=100`, `UnitPrice=4.5000`).

| Pin | Value |
|---|---|
| App | `http://localhost:5000` (`dotnet run --launch-profile http --no-build`, DLLs 11 Nov 09:48 — after G1–G5 sources of 10 Nov) |
| User | Demo Admin (`admin` / company `DEMO`) |
| HEAD | `e24f54069316a346171fed7f14017c09d593cb95` + uncommitted G1–G5 |
| Tenant data | 0 `SaDO`. 1 SO (`SO2610-0001` NEW, WINCOM, FG001 @ 4.50 vs master 4.00). 4 posted invoices for missing customer `C001`, all zero price / zero amount2. |

This is **not** 10/10 and does not replace isolated SQL Server P01–P08.

---

## Executed

| Case | Result |
|---|---|
| **Price Inquiry copy** `/sales/price-inquiry` | **PASS.** Hint: “This shows what current price rules would select today for a new direct line. It is not a recorded agreed document price.” Currency NullText is “Base currency”. |
| **Price Info** on `SO2610-0001` line 1 | **PASS.** Dialog **Selling Price Information**. **RECORDED AGREED PRICE:** `RM 4.5000 / PCS · Not recorded · Discount None · Manual price`. **WHAT CURRENT RULES WOULD SELECT TODAY** auto-ran: recorded `RM 4.5000 · Not recorded` vs current `RM 4.0000 · Item Default`. Copy: “The document price has NOT been changed.” Ladder: Customer Special / lists unused; Item Default used at 4.00. **RECHECK CURRENT RULES** present. One click on the line Price Info control; no extra confirmation. |
| **Qty change on existing line** (C19) | **PASS.** Edit line FG001. Qty 100 → 110. Unit price stayed **4.5000** (did not live-resolve to 4.00). Preview **Amount 495.00 · Tax 0.00 Net 495.00**. Popup **Close** without Update/Save. SQL after: `OrderQty=100`, `UnitPrice=4.5000`. |
| **Invoice FROM DELIVERY ORDER** | **PASS for launcher / extra-click.** New invoice shows SOURCE + **FROM DELIVERY ORDER** as the primary action. One click opened popup **From Delivery Order**. Tenant has **zero** posted DOs, so commercial slot copy (six % + amounts + tax group + OrderType) was **not** live-exercised. Closed without saving. |
| **CN amount2 warning** | **NOT RUN (tenant data).** New CN **Copy from invoice** stays disabled until a resolved customer. Posted invoices are `C001` with **no** `SaCust` row and `ItemDiscAmount1=0`. WINCOM has no posted invoice. Did not create or post documents to manufacture this case. |

---

## Not claimed

- DO→Invoice commercial fidelity with discounts/basis on a real billed DO.
- Linked DO/Invoice qty freeze (SO existing-line freeze was used instead).
- CN copy of a posted invoice, override-not-redeclared, or amount2 warning toast.
- Price Inquiry **Explain price** with a selected customer/item (copy only).
- SQL Server P01–P08, origin rebase, A6, Phase B.

---

## GO / NO-GO

| Question | Answer |
|---|---|
| UI copy and Price Info recorded vs current-rule | **GO** on this tenant |
| Existing-line qty keeps unit price; amounts recompute | **GO** (SO popup, not saved) |
| DO picker extra-click | **GO** (empty list) |
| Full G5 UAT matrix including DO copy + CN amount2 | **NO-GO** until a tenant has a posted billable DO and a posted invoice with `ItemDiscAmount1 <> 0` |
| Merge / 10/10 | Still **NO-GO** (rebase + SQL Server still required) |
