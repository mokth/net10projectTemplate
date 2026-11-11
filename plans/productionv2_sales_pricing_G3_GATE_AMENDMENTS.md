# G3 binding amendments (A3 discount class + failed lookup)

**Authority:** Gate 0 on `584411e317ac6990ed8a5f0886207779d821deaf`.  
**Applies before any G3 code.** v2.1 §A3 / R03 / R04 text is overridden here.  
**G3 is not authorized to start** until G0 is independently reviewed and G1–G2 have passed.

These amendments exist because a literal reading of v2.1 would either miss real discount failures, block legitimate zero-quantity edits, or match class rules with a case-sensitive compare against client `Classification`.

---

## 1. Discount-failure scope (replaces R03 / A3 fail-closed wording)

Current code, [`SaSalesRefService.ItemFamily.PriceSources.cs`](../ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs) 63–78:

```csharp
var discount = await ResolveItemDiscountAsync(...);
var selection = discount.Succeeded && discount.Data is not null
    ? discount.Data
    : SaItemFamilyDiscountSelection.None;
return IvMasterOperationResult<SaLinePricingResult>.Ok(...);
```

[`ResolveItemDiscountAsync`](../ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Resolve.cs) outcomes:

| Outcome | What happens today | Required G3 behavior |
|---|---|---|
| Validation Fail (null request, bad company context, blank item, `Qty <= 0`) | `Succeeded == false`; price method substitutes `None` and still returns **Ok** with the resolved price and zero discounts | Treat as a **pricing error**. Do not silently zero discounts. |
| Successful `None` (rules loaded, no match) | Ok + `Found == false` | **Keep.** This is a valid no-discount. |
| `Data == null` with `Succeeded == true` | Treated as None | Treat as lookup failure (should not occur; fail closed if it does). |
| Database / unexpected exception from `ToListAsync` | Thrown; not converted to None | **Do not catch and convert to zero discount.** Let it fail the request. |

Do **not** describe this as “DB failure becomes 0%.” A thrown exception already fails the request. The silent hole is only the `Succeeded == false` substitution.

**Zero-quantity UI:** `OnPopupQuantityChangedAsync` can send `Qty <= 0` while the operator is editing. Today that is a discount validation Fail that is swallowed, so the line still gets a price with no discount. After G3, the same call would block pricing. Measure the current popup behavior in G3 tests before changing it. If blocking a blank/zero qty edit is a workflow regression, fail closed only when `Qty > 0` (or skip the discount query until qty is positive) and still fail closed for company/item context failures. Do not leave `Succeeded == false` mapped to `None` for those context failures.

No change to `SaInvoiceCalc.CalculateDiscountPerUnit`, JOIN vs sequential arithmetic, slot order, or rounding.

---

## 2. Class match (replaces A3 `rule.IClass == actualClass`)

v2.1 wrote a case-sensitive equality. Live overlap uses trim + ordinal-ignore-case:

```672:675:ErpWeb.Core/Sales/SaItemFamilyPricing.cs
    public static bool ClassesOverlap(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ||
        string.IsNullOrWhiteSpace(right) ||
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
```

**Runtime apply (directional, G3):**

- Load `IvStockMaster.IClassCode` on the **server** for the tenant item. Do not trust `SaLinePricingRequest.IClass`, UI `Classification`, or a missing DO IClass.
- A blank/whitespace rule class applies to every item (wildcard).
- A non-blank rule class applies only when actual `IClassCode` is non-blank and  
  `string.Equals(rule.IClass.Trim(), actualClass.Trim(), StringComparison.OrdinalIgnoreCase)`.
- A blank actual class does **not** match a class-specific rule.

Keep existing **symmetric** `ClassesOverlap` for discount-master overlap/conflict on save.

Prove in tests that:

1. Rule `IClass = "c1"` matches item `IClassCode = "C1"`.
2. Rule class `"C1"` does not match item class `"C2"`.
3. Blank rule class matches any actual class, including blank.
4. Class-specific rule does not match blank actual class.
5. Client `Classification = "C1"` with `IClassCode = "C2"` must follow **IClassCode**, not Classification.
6. DO (today sends no IClass) must still select using server IClassCode after G3.

Query only the IClassCode needed. Do not update Item Master.

---

## 3. A4 commercial UOM (same G3 gate in v2.1)

Align **new direct** DO/Invoice/CN price-request UOM to `SellingUom ?? StdUom`, same as QT/SO.

Do not persist a new selling UOM onto `SaInvoiceDetail` (column does not exist). Do not pass billable-DTO `StdUom` into the resolver as a commercial UOM. Do not change `ResolveFrozenStdFactor`, `IvQty`, pack snapshots, or the SO identity check except the **commercial eligibility** comparison called out in v2.1 R10 — and only after G0 frozen-pack lines stay untouched.

If G3 also adjusts `SaInvoiceService` ~2295–2306, change only the compared selling-UOM source (saved SO snapshot vs current master). Stop if the hunk includes pack, StdQty, warehouse, lot, posting, or allocation.

---

## 4. Tests G3 must add (in addition to C03–C05 / C12)

- Failed `ResolveItemDiscountAsync` (company context / blank item) while price candidates exist → whole `ResolveLinePricingAsync` fails; discounts are not zeroed.
- Successful no-rule → price Ok, discounts zero.
- Qty≤0 path: document the chosen UI contract; no silent zero-discount if a price is still returned.
- Directional class tests listed in §2.
- Direct BOX selling UOM vs PCS standard: same commercial price as SO; StdQty/pack unchanged.

Physical snapshots P01–P04 must be equal to the G0 sqlite baseline for the same fixtures.
