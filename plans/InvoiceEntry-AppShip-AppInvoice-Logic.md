# Invoice Entry — `AppInvoice` / `AppShip` Address Logic

Source: `InvoiceEntry.aspx` (client `popuplateCustInfo`), `InvoiceEntry.aspx.cs` (`LoadCustomerInfo`), Customer Profile (`SaCust`).

Use this when porting customer-select address behaviour to another Blazor ERP app.

---

## 1. What the two columns mean

Both flags live on the **customer master** (`SaCust`). Invoice Entry does **not** persist them. They are only read when the user **selects a customer**.

| `SaCust` column | Customer Profile UI | Meaning |
|---|---|---|
| **`AppInvoice`** | Apply To Invoice | Use the customer’s **default address** as the **billing** address |
| **`AppShip`** | Apply To Shipment | Use the customer’s **default address** as the **shipping** address |

Default for a **new** customer: **both `true`**.

When a flag is `true`, the extra address tab on Customer Profile is hidden. When `false`, a separate address is stored on `SaCust`:

| Flag | Value | Extra tab | Stored on `SaCust` |
|---|---|---|---|
| `AppInvoice` | `false` | Invoice address tab shown | `InvName`, `InvAddress1–4`, `InvCity`, `InvState`, `InvPostalCode`, `InvCountry`, `InvTel`, `InvFax` |
| `AppShip` | `false` | Shipping address tab shown | `ShipName`, `ShipAddress1–4`, `ShipCity`, `ShipState`, `ShipPostalCode`, `ShipCountry`, `ShipTel`, `ShipFax` |

The **default / registered address** is always:

- `CustName`
- `Address1`, `Address2`, `Address3`, `Address4`
- `City`, `State`, `PostalCode`, `Country`
- `Tel`, `Fax`

---

## 2. Naming trap on Invoice Entry

Control names on the invoice screen are **reversed** from the UI labels. Do not map by control name; map by meaning.

| Invoice UI tab | Client controls | Saved on invoice header (`SaInv`) |
|---|---|---|
| **BILLING ADDRESS** | `txtShipName`, `txtShipAddress1–4`, `txtShipCity`, `txtShipState`, `txtShipPostalCode`, `txtShipCountry`, `txtShipTel`, `txtShipFax` | `InvName`, `InvAddress1–4`, `InvCity`, `InvState`, `InvPostalCode`, `InvCountry`, `InvTel`, `InvFax` |
| **SHIPPING ADDRESS** | `txtDeliverName`, `txtDeliverAdd1–4`, `txtDeliverCity`, `txtDeliverState`, `txtDeliverPostalCode`, `txtDeliverCountry`, `txtDeliverTel`, `txtDeliverFax` | `ShipName`, `ShipAddress1–4`, `ShipCity`, `ShipState`, `ShipPostalCode`, `ShipCountry`, `ShipTel`, `ShipFax` |

Summary:

- **`AppInvoice`** → fills **billing** (`txtShip*` → `SaInv.Inv*`)
- **`AppShip`** → fills **shipping** (`txtDeliver*` → `SaInv.Ship*`)

---

## 3. When the user selects a customer

### 3.1 Where the live logic runs

Invoice Entry copies addresses **in JavaScript** (`popuplateCustInfo` in `InvoiceEntry.aspx`).

The same `if (appInv)` / `if (appShip)` block in `LoadCustomerInfo()` (`InvoiceEntry.aspx.cs`) is **commented out**. The server callback still runs for **non-address** work (clear lines, currency, remarks).

Sales Order, DO, CN, and Quotation still apply the same address rules **on the server**.

### 3.2 Field list used on customer select

```
CustCode; CustName; Address1; Address2; Address3; Address4; City; State; PostalCode; Country;
Tel; Fax; PayCode; Currency; TaxGrCode; AppShip; AppInvoice; ShipName; ShipAddress1; ShipAddress2;
ShipAddress3; ShipAddress4; ShipCity; ShipState; ShipPostalCode; ShipCountry; ShipTel; ShipFax;
InvName; InvAddress1; InvAddress2; InvAddress3; InvAddress4; InvCity; InvState; InvPostalCode;
InvCountry; InvTel; InvFax; GroupDiscount; DiscountMethod; DiscountSeq; SRepCode; InvPrefix;
TaxGroup; DecPoint; ContactPerson
```

Relevant indexes:

| Index | Field |
|---|---|
| 1 | `CustName` |
| 2–11 | Default `Address1–4`, `City`, `State`, `PostalCode`, `Country`, `Tel`, `Fax` |
| 15 | `AppShip` |
| 16 | `AppInvoice` |
| 17–27 | Dedicated ship: `ShipName`, `ShipAddress1–4`, `ShipCity`, `ShipState`, `ShipPostalCode`, `ShipCountry`, `ShipTel`, `ShipFax` |
| 28–38 | Dedicated invoice: `InvName`, `InvAddress1–4`, `InvCity`, `InvState`, `InvPostalCode`, `InvCountry`, `InvTel`, `InvFax` |

### 3.3 Truth table

| Flag | Value | Billing (`txtShip*` / `SaInv.Inv*`) | Shipping (`txtDeliver*` / `SaInv.Ship*`) |
|---|---|---|---|
| **AppInvoice** | **true** | Copy **default** (`CustName` + `Address1–4` + city/state/postal/country/tel/fax) | — |
| **AppInvoice** | **false** | Copy **invoice address** (`InvName` + `InvAddress1–4` + `InvCity` …) | — |
| **AppShip** | **true** | — | Copy **default** (`CustName` + `Address1–4` + …) |
| **AppShip** | **false** | — | Copy **ship address** (`ShipName` + `ShipAddress1–4` + `ShipCity` …) |

The two flags are **independent**.

| Case | Billing | Shipping |
|---|---|---|
| Both `true` (default) | Registered customer address | Registered customer address |
| `AppInvoice` false, `AppShip` true | Dedicated `Inv*` address | Registered customer address |
| `AppInvoice` true, `AppShip` false | Registered customer address | Dedicated `Ship*` address |
| Both `false` | Dedicated `Inv*` address | Dedicated `Ship*` address |

---

## 4. Other work on customer change (not from the flags)

After JS fills addresses, Invoice Entry calls `panelCust.PerformCallback("CUSTOMER:" + custCode)`. `LoadCustomerInfo` still:

1. Sets `CustomerCode`
2. **Clears Ship To** (`DeliverTo` / `SaCustAdd` lookup) so a previous customer’s extra address is not kept
3. Clears the per-customer item-price session
4. Recalculates currency rate
5. Clears remarks / Ref1–4 / custom import-export numbers
6. **Deletes all invoice lines** and recalculates totals
7. Applies invoice prefix if `AdPara.PrefixControl = 1`

JS also copies:

- Term of payment (`PayCode`)
- Currency
- Tax group
- Salesman (`SRepCode`)
- Contact person
- Discount method / sequence
- Decimal-point flag (`DecPoint`)
- Invoice prefix (`InvPrefix`)

---

## 5. Extra ship-to after that (`SaCustAdd`)

On the **SHIPPING ADDRESS** tab, **SHIP TO** (`txtDeliverTo`) lists `SaCustAdd` for that customer.

Picking a row **overwrites only shipping** (`txtDeliver*`). Billing is left alone.

This is **independent of `AppShip`**.

---

## 6. When **not** to re-apply `AppInvoice` / `AppShip`

Do **not** re-run these flags when:

1. **Opening an existing invoice** — addresses come from the invoice header (`SaInv.Inv*` / `SaInv.Ship*`)
2. **User picks a `SaCustAdd` ship-to** — shipping is overwritten from that row; billing stays
3. **Copying from SO / DO** — those documents’ addresses replace the customer-default copy

---

## 7. Save mapping (invoice header)

When saving the invoice:

| Invoice form | `SaInv` columns |
|---|---|
| Billing (`txtShip*`) | `InvName`, `InvAddress1–4`, `InvCity`, `InvState`, `InvPostalCode`, `InvCountry`, `InvTel`, `InvFax` |
| Shipping (`txtDeliver*`) | `ShipName`, `ShipAddress1–4`, `ShipCity`, `ShipState`, `ShipPostalCode`, `ShipCountry`, `ShipTel`, `ShipFax` |
| Customer name | `CustName` (from `txtCustName`, not from billing/shipping name) |

Values are stored **uppercase** on save in WebForms.

---

## 8. Logic to apply in Blazor

On customer select:

```csharp
void ApplyCustomerAddresses(SaCust c, InvoiceHeader inv)
{
    // Billing
    if (c.AppInvoice)
        CopyAddress(fromDefault: c, to: inv.Billing);
    else
        CopyAddress(fromInvoice: c, to: inv.Billing);

    // Shipping
    if (c.AppShip)
        CopyAddress(fromDefault: c, to: inv.Shipping);
    else
        CopyAddress(fromShip: c, to: inv.Shipping);

    inv.ShipToCode = null; // clear SaCustAdd selection
}

void OnShipToSelected(SaCustAdd add, InvoiceHeader inv)
{
    CopyAddress(from: add, to: inv.Shipping); // billing unchanged
}

void CopyAddress(/* name, address1-4, city, state, postal, country, tel, fax */)
{
    // Copy the 11 fields listed above into the target address block
}
```

Address field pairs:

| Target | Source when flag = true | Source when flag = false |
|---|---|---|
| Billing name | `CustName` | `InvName` |
| Billing address 1–4 | `Address1–4` | `InvAddress1–4` |
| Billing city / state / postal / country | `City` / `State` / `PostalCode` / `Country` | `InvCity` / `InvState` / `InvPostalCode` / `InvCountry` |
| Billing tel / fax | `Tel` / `Fax` | `InvTel` / `InvFax` |
| Shipping name | `CustName` | `ShipName` |
| Shipping address 1–4 | `Address1–4` | `ShipAddress1–4` |
| Shipping city / state / postal / country | `City` / `State` / `PostalCode` / `Country` | `ShipCity` / `ShipState` / `ShipPostalCode` / `ShipCountry` |
| Shipping tel / fax | `Tel` / `Fax` | `ShipTel` / `ShipFax` |

Also on customer change (same as WebForms):

- Clear line items
- Clear remarks / refs
- Recalculate currency rate
- Reload item price session for the new customer
- Copy pay term, currency, tax group, salesman, contact, discount method/seq, prefix

Do **not** re-run `AppInvoice` / `AppShip` when opening an existing invoice, after `SaCustAdd` ship-to pick, or after copy from SO/DO.
