---
name: Sales master UI (hardened v3)
overview: Payment term / sales rep / tax group CRUD cloning Currency/Country — fail-closed CompanyCode, EF DbSet + decimal scale, InventoryLeftoverSite.Apply only, Save(code,vm,isNew,expectedFingerprint) with tracked reload, AnyAsync duplicate on create, paging lock-in, Activate=EDIT, Delete=DELETE+CanDelete.
todos:
  - id: ef-entities-dbsets
    content: Verify/add SaPaymentTerm, SaSalesRep, SaTaxGroup entities + DbSets; HasPrecision for decimals
    status: pending
  - id: leftover-apply
    content: InventoryLeftoverSite.Apply(row, ctx) overloads for SaPaymentTerm and SaSalesRep only
    status: pending
  - id: service-dtos-crud
    content: List/Get/Save(code,vm,isNew,expectedFingerprint)/CanDelete/Delete/Activate with fail-closed company, tracked fingerprint, AnyAsync create dup
    status: pending
  - id: ui-pages
    content: Three list pages; pass fingerprint into Save; Delete=DELETE+CanDelete; Activate=EDIT
    status: pending
  - id: menus
    content: MenuCodes, menus.xml, init-menu-access.sql ADD/EDIT/DELETE
    status: pending
  - id: tests
    content: Expand tests for fingerprint-in-Save, AnyAsync dup, empty company, IDOR, decimal scale, paging lock-in
    status: pending
isProject: false
---

# Sales master UI — hardened v3 (Architect 9.3 → target ≥9.6)

Closes the three 9.3 leftovers on top of v2.

Shell: `SaAreaList.razor` + `SaCodeRefListPageBase.cs`. Service first.

| Master | Clone | Scope | Active |
|---|---|---|---|
| Payment term | Currency | `(CompanyCode, PayCode)` | Yes |
| Sales rep | Currency | `(CompanyCode, SrepCode)` | Yes; wide popup |
| Tax group | Country | Global | No Active |

No `SaCustTypeList` / `RowVersion`.

---

## 0. EF gate + decimal scale

Confirm/add entities + DbSets on `AppDbContext`. `Percentage` / `CommissionRate`: `HasPrecision(9, 4)` (or DB exact). UI `DxSpinEdit` + service reject excess scale. Field lengths: Code 20, Desc/Name 100, Address 100, City/State/Country 50, Postal 20, Phone 30, Email 100. `NormalizeCode` = Trim+Upper.

---

## 1. Leftover stamp — Apply-only template (9.3 flaw #2)

**Only** this call site pattern (match Currency exactly):

```csharp
InventoryLeftoverSite.Apply(row, _ctx);
```

Add overloads for `SaPaymentTerm` and `SaSalesRep` wherever Currency’s `InventoryLeftoverSite.Apply` already lives. Do **not** show `InventoryTenantContext.Apply(...)` in templates. Tax group: no Apply.

---

## 2. Fail-closed CompanyCode

```csharp
void EnsureCompanyContext()
{
    if (string.IsNullOrWhiteSpace(_ctx.CompanyCode))
        throw new InvalidOperationException("Company context required.");
}
```

Call at start of every company-scoped method (and tax-group delete ref-counts).

---

## 3. Save — fingerprint inside service on tracked reload (9.3 flaw #1)

Fingerprint is **not** page-only after `AsNoTracking` Get. It is enforced **inside Save** on a **tracked** entity reload, with `expectedFingerprint` as a Save argument.

```csharp
Task SavePaymentTermAsync(
    string code,
    SaPaymentTermEditVm vm,
    bool isNew,
    string? expectedFingerprint, // required when isNew == false
    CancellationToken ct = default);

public async Task SavePaymentTermAsync(
    string code, SaPaymentTermEditVm vm, bool isNew, string? expectedFingerprint, CancellationToken ct = default)
{
    EnsureCompanyContext();
    code = NormalizeCode(code);
    ValidatePaymentTerm(vm, isNew, code);

    if (isNew)
    {
        // Explicit duplicate check — Area parity (9.3 flaw #3)
        var exists = await _db.SaPaymentTerms.AnyAsync(
            x => x.CompanyCode == _ctx.CompanyCode && x.PayCode == code, ct);
        if (exists) throw DuplicateKey(/* same message as Area */);

        var row = new SaPaymentTerm
        {
            PayCode = code,
            /* map mutable fields from vm */,
            IsActive = vm.IsActive ?? true
        };
        InventoryLeftoverSite.Apply(row, _ctx);
        _db.SaPaymentTerms.Add(row);
    }
    else
    {
        if (string.IsNullOrEmpty(expectedFingerprint))
            throw new ArgumentException("expectedFingerprint required for update.");

        // TRACKED reload (no AsNoTracking) — concurrency + mutate same instance
        var row = await _db.SaPaymentTerms
            .FirstOrDefaultAsync(x => x.CompanyCode == _ctx.CompanyCode && x.PayCode == code, ct)
            ?? throw NotFound();

        // Compare via MapEdit so FP matches what the page stored from Get
        if (Fingerprint(MapEdit(row)) != expectedFingerprint)
            throw ConcurrencyConflict();

        // Immutable code by omission — never assign row.PayCode from vm
        MapMutableOnto(row, vm); // Desc, Days, IsActive only
    }

    try { await _db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) { throw TranslateDuplicate(ex); }
}

static string Fingerprint(SaPaymentTermEditVm vm) =>
    $"{vm.Desc}|{vm.Days}|{vm.IsActive}";
// Save update path: Fingerprint(MapEdit(row)) — same function the page used after Get
```

**UI:** on popup open, `_loadedFingerprint = Fingerprint(vm)` from Get (AsNoTracking OK for display). On save edit, pass `_loadedFingerprint` into `Save*(code, vm, isNew: false, expectedFingerprint)`. On conflict toast + reload. Do **not** re-check fingerprint only in the page after Get — service is the authority.

Same shape for sales-rep and tax-group Save (tax-group: no company on key, no Apply; still tracked reload + fingerprint + AnyAsync on create).

Get/Delete/Activate: `CompanyCode == _ctx.CompanyCode` after `EnsureCompanyContext()` (pay-term/sales-rep).

---

## 4. Explicit AnyAsync duplicate on create (9.3 flaw #3)

Every create path runs `AnyAsync` on the natural key **before** Add (Area parity). `DbUpdateException` translation remains as belt-and-suspenders, not the only duplicate check.

---

## 5. Paging lock-in

Copy Currency/Country List signature exactly; no new `PagedResult` unless twin has it. Always `AsNoTracking()` on List/Get reads.

---

## 6. UI authz + CanDelete

| Action | Right |
|---|---|
| Activate | `MenuAccess.Edit` |
| Delete | `MenuAccess.Delete` **and** `CanDelete*` |
| Save new / edit | Add / Edit |

```csharp
async Task OnDeleteConfirmedAsync(string code)
{
    if (!Menu.Has(menuCode, MenuAccess.Delete)) return;
    if (!await SalesRef.CanDeletePaymentTermAsync(code)) { Toast.Error("Referenced."); return; }
    await SalesRef.DeletePaymentTermAsync(code);
    await ReloadGridAsync();
}
```

Service Delete re-checks CanDelete. Pages: `SaPaymentTermList`, `SaSalesRepList`, `SaTaxGroupList` (+ cs). Routes `/sales/payment-terms|sales-reps|tax-groups`.

Delete refs: pay-term Cust/Invoice/DisGroup PayCode; sales-rep Cust/Invoice SalesmanCode; tax group Cust/Invoice/InvoiceDetail TaxGrCode (current-company).

---

## 7. Menus

`SA_PAY_TERM` / `SA_SALES_REP` / `SA_TAX_GROUP` under `SA_MASTER`; MenuCodes + init-menu-access ADD/EDIT/DELETE.

---

## 8. Tests

| Case | Assert |
|---|---|
| Create | succeeds; **AnyAsync** dup rejected |
| Empty CompanyCode | fail-closed throw |
| Cross-company Get/Save/Delete/Activate | not found / no mutate |
| Update | mutable fields change; code unchanged |
| Fingerprint mismatch | Save throws conflict (service-level, tracked path) |
| Missing expectedFingerprint on update | throws |
| CanDelete true → Delete | succeeds |
| Decimal scale | excess scale rejected |
| List | twin paging contract + AsNoTracking |

---

## 9. Out of scope

No IvMsCode retarget; no SQL rowversion; DBA applies `init-sales-masters.sql` manually.

---

## Acceptance

`InventoryLeftoverSite.Apply(row, _ctx)` only; Save takes `expectedFingerprint` and compares on **tracked** reload; create uses explicit `AnyAsync`; fail-closed company; Delete=DELETE+CanDelete; Activate=EDIT; tests green.
