# Sales Item Family — Phase 0 Findings

**Database:** `ERPWeb` on `LAPTOP-N3NQSB16\SQLEXPRESS` (SQL Server 16.00.1000)  
**Audit date:** 2026-09-15  
**Method:** read-only `sys.*` queries + whole-solution code verification (no writes to the live DB)  
**Phase 0 result:** **PASS** — no STOP item. Owner signature of the decision register remains the gate for Phase 1.

**Scope covered:** `IvCustPriceGroup`, `IvCustPrice`, `SaItemCust`, `SaDisGroupItem`, plus every enabling object (`IvMas`, `IvMasPack`, `IvStockMaster`, `MsUOM`, `IvClass`, `SaDisGroup`, `SaDisCust`, `SaCust`) and the code that would touch them.

---

## 1. Summary matrix

| Object | Exists live | Rows | Concurrency | Verdict |
|---|---|---|---|---|
| `IvMas` | **ABSENT** | — | — | not an authority; no Blazor type either |
| `IvMasPack` | **ABSENT** | — | — | absent |
| `IvStockMaster` | PRESENT | 1 | `RowVersion` | **sole item authority** |
| `IvCustPriceGroup` | **ABSENT** | — | — | create (our schema, R6-branch “table absent”) |
| `IvCustPrice` | **ABSENT** | — | — | create |
| `SaItemCust` | **PRESENT** | **0** | none | **alter** (widen key + add `RowVersion`) |
| `SaDisGroupItem` | **ABSENT** | — | — | create |
| `SaDisGroup` | PRESENT | 0 | Level B | shipped (v1) — verification only |
| `SaDisCust` | PRESENT | 0 | Level B | shipped (v1) — verification only |
| `MsUOM` | PRESENT | 1 | `RowVersion` | UOM source |
| `IvClass` | PRESENT | 1 | `RowVersion` | class source |
| `SaCust` | PRESENT | 1 | `RowVersion` | consumer (`CustPriceCode`, `PriceMethod`) |

A name-variant scan found **no** other related tables (`%CustPrice%`, `%DisGroup%`, `%ItemCust%`, `%CustItem%`, `%byCust%`, `%Discount%`, `%IvMas%` → only `SaDisGroup`, `SaItemCust`).

### Consequence for the DDL branch (§13.2 of the plan)

Three of the four tables are **absent**, so the key-widening question (Branch A vs B) is moot for them: we define the PK ourselves. `SaItemCust` is the only existing table, and its key is narrower than intended → **Branch A applies, and it is safe because the table holds 0 rows.**

---

## 2. Item authority — RESOLVED

- `IvMas` **ABSENT**, `IvMasPack` **ABSENT**, `IvStockMaster` **PRESENT** (1 row).
- Code: no `IvMas` type exists anywhere in `ErpWeb.Model` / `ErpWeb.Core` (grep clean).
- **Conclusion:** `IvStockMaster` is the single item authority. The `IvMas`-vs-`IvStockMaster` question is closed with no legacy table to declare read-only, and no second writer exists in this repository.

---

## 3. `SaItemCust` — PRESENT, 0 rows (the only existing table)

**Key reality**
```text
PK_SaItemCust: (ICode, CustCode, SellingUOM, MOQ)   clustered, unique
```
- **Answer to logic-spec Q1:** the live PK is the **4-part** key, not the designer's `(ICode, CustCode)`.
- `CompanyCode` is **not** in the PK and is nullable → the table cannot distinguish companies today.

**Column matrix** (bytes ÷ 2 = nvarchar chars)

| Column | Live type | Null | Note |
|---|---|---|---|
| `ICode` | nvarchar(20) | NOT NULL | PK |
| `IDesc` | nvarchar(200) | NULL | |
| `CustCode` | nvarchar(20) | NOT NULL | PK |
| `CustICode` | nvarchar(100) | NOT NULL | customer part no |
| `InvDesc` | nvarchar(300) | NULL | |
| `SellingUOM` | nvarchar(5) | NOT NULL | PK — matches live `MsUOM.UOMCode`(5) |
| `UnitPrice` | **float** | NULL | keep type; explicit service-side scale (§8.5) |
| `Currency` | nvarchar(5) | NULL | mismatch ⇒ fail closed (§7.1) |
| `StdCustPSize` | **float** | NULL | advisory only |
| `Status` | nvarchar(10) | NULL | **refresh state** (`NEW`/`FALSE`), not activation |
| `Created` / `Updated` | datetime | NULL | audit |
| `UserID` / `UpdatedUID` | nvarchar(10) | NULL | audit |
| `SPart` | bit | NULL | always `false` from the legacy save |
| `CompanyCode` / `BranchCode` / `LocationCode` | nvarchar(5) | NULL | tenant stamp; **not** in PK |
| `DG` / `SG` | nvarchar(5) | NULL | strings, read as int elsewhere — still unexplained (logic-spec Q5) |
| `MOQ` | **int** | NOT NULL | PK; integer MOQ ⇒ fractional MOQ unsupported (documented) |
| `ProjID` | nvarchar(20) | NULL | never written by the legacy screens |
| **`CustModel`** | nvarchar(10) | NULL | **not in the legacy spec at all** — decision required (carry or drop) |
| — | — | — | **no `RowVersion`** |

**Required changes (Branch A)**

| Change | Action | Risk |
|---|---|---|
| add `CompanyCode` to the PK | drop/recreate PK as `(CompanyCode, CustCode, ICode, SellingUOM, MOQ)` | **none in this DB (0 rows)**; script must abort if rows exist without a single-company guarantee |
| add `RowVersion` | additive `rowversion` column | none |
| keep the business key unique | PK already enforces it | none |

**Origin:** the table is neither modelled in `ErpWeb.Model` nor created by any script in `scripts/` → it came with the database (legacy schema). The new script must therefore be **alter-first for this table**, never “create if absent” only.

---

## 4. `IvCustPriceGroup` — ABSENT ⇒ we define it

| Item | Decision (from live widths) |
|---|---|
| PK / business key | `(CompanyCode, CustPriceCode)` |
| `CustPriceCode` width | **nvarchar(20)** — matches the live consumer column `SaCust.CustPriceCode` nvarchar(20); do **not** use the legacy `nvarchar(10)`, which would reject codes the customer master can already hold |
| `CustPriceDesc` | nvarchar(50) |
| tenant | `CompanyCode` key; `BranchCode`/`LocationCode` nvarchar(5) stamps |
| audit | `Created`/`Updated` datetime, `UserID`/`UpdatedUID` nvarchar(10) |
| additive | `Active` bit NOT NULL default 1; `RowVersion` rowversion |
| legacy `Editable` | table is absent → **do not carry it** unless a consumer needs it (decision required) |

## 5. `IvCustPrice` — ABSENT ⇒ we define it

| Item | Decision (from live widths) |
|---|---|
| PK / business key | `(CompanyCode, CustPriceCode, ICode, UOM)` |
| `ICode` | nvarchar(20) — matches live `IvStockMaster.ICode` **and** `SaItemCust.ICode` (both 20) |
| `UOM` | **nvarchar(5)** — matches live `MsUOM.UOMCode` nvarchar(5) |
| `SellingPrice` | **decimal(18,4)** — matches live `IvStockMaster.SellingPrice` decimal(18,4) |
| `SellPackSize` | decimal(18,4), advisory |
| `IDesc` | nvarchar(200) (denormalised, as legacy) |
| tenant / audit / additive | as §4 (`RowVersion` on the header only; no per-line version) |

## 6. `SaDisGroupItem` — ABSENT ⇒ we define it (and avoid the legacy traps)

| Item | Decision |
|---|---|
| PK | `ID int IDENTITY` — **`int`, not the designer's `smallint`** (32,767 ceiling would cap the table) |
| business key (unique) | `(CompanyCode, ICode, IClass, QtyFr, QtyTo, DateFr)` |
| `QtyFr`/`QtyTo` | **decimal(18,4)**, NOT NULL; `QtyFr > 0`, `QtyTo >= QtyFr`; inclusive match |
| `Discount`/`Discount1` | decimal(18,4) |
| `DiscountType`/`DiscountType1` | nvarchar(10), `PERCENTAGE` \| `AMOUNT` |
| `DateFr`/`DateTo` | **`date`**, `DateFr` NOT NULL, `DateTo` NULL = open-ended; inclusive on the date part |
| `IClass` | nvarchar(10) — matches live `IvClass.IClassCode` nvarchar(10); blank = all classes |
| `EffectPrice` | nvarchar(10), `DEALER` \| `SELLING`; stored, **inert** (§7 of the plan) |
| `GroupStatus` | carry as nvarchar(10) for compatibility, **not authoritative**, not exposed |
| `GroupName` / `GroupLevel` | **not carried** — verified write-only/never-read in legacy, and `SaDisGroup` already has exactly one Blazor implementation |
| tenant / audit / additive | `CompanyCode` key; stamps; `RowVersion`; overlap check inside a `Serializable` transaction |

## 7. Enabling masters — live widths (EF-config drift noted)

| Object | Live | EF config says | Impact |
|---|---|---|---|
| `MsUOM.UOMCode` | **nvarchar(5)** | 10 | new UOM columns must be **(5)** |
| `MsUOM.UOMDesc` | nvarchar(20) | 100 | drift only |
| `IvClass.IClassCode` | **nvarchar(10)** | — | new `IClass` columns = (10) |
| `IvStockMaster.ICode` | **nvarchar(20)** | 30 | new `ICode` columns = (20) |
| `IvStockMaster.SellingUOM` | nvarchar(10) | 10 | item default UOM; `StdUOM` is (5) |
| `IvStockMaster.SellingPrice` | **decimal(18,4)** | 18,4 | masters’ price columns mirror this |
| `IvStockMaster.Active` | bit NULL | — | active filter is on this column |

**Drift warning (repeat of the known trap):** `HasMaxLength` in EF does **not** alter an existing SQL column, so a new column must be created at the *live* width, not at the EF-configured width.

## 8. Customer side (`SaCust`) — widths and current data

| Column | Live |
|---|---|
| `CustPriceCode` | nvarchar(20), NULL |
| `GroupDiscount` | nvarchar(20), NULL |
| `PriceMethod` | nvarchar(50), NULL |
| `DiscountMethod` | nvarchar(20), NULL |
| `Currency` | nvarchar(10), NULL |
| `DecPoint` | bit NULL |
| `Active` | bit NOT NULL |

Only one customer row exists: `DEMO / WINCOM`, `PriceMethod = "FOLLOW SELLING PRICE X DISCOUNT"`, `DiscountMethod = JOIN`, `Currency = MYR`, `CustPriceCode` blank.

- **Dealer-mode customers: 0** in this database → the “fail closed or interim-map” question (§7.3 of the plan) has **no migration impact here**, but the production DB must be re-checked when it is not this dev database.
- **`CustPriceCode` non-blank: 0** → no D-6 clause-3 tolerance is required in this DB; the rule still stays for production.

## 9. Dealer price — CONFIRMED ABSENT

A schema-wide scan for any column matching `%dealer%` (and `%price%`) found **no dealer-price column**. The only price-bearing item/customer columns in the entire database are:

```text
IvStockMaster.SellingPrice, IvStockMaster.PurchasePrice, SaItemCust.UnitPrice   (plus document/transaction prices)
```

**Conclusion:** dealer pricing is **unsupported** (plan §7 step 0 / D6) — this is now a verified fact, not an assumption. `SaCust.PriceMethod = 'FOLLOW DEFAULT DEALER PRICE'` and `SaDisGroupItem.EffectPrice = 'DEALER'` remain stored values with no physical source.

## 10. Data-quality checks that were expected to be noisy — all empty

| Check | Result |
|---|---|
| overlapping `SaDisGroupItem` rules per item | **N/A** (table absent) |
| live `QtyFr = 0 AND QtyTo = 0` rows | **N/A** (table absent) |
| duplicate business keys | **none** (`SaItemCust` 0 rows; other tables absent) |
| invalid/NULL tenant values | **N/A** (0 rows in `SaItemCust`; others absent) |
| `SaItemCust.Status` distribution | N/A (0 rows) — semantics still preserved by design |

**Conclusion:** the deterministic tie-break (§8.3.1 of the plan) has nothing to resolve in *this* database, but it stays implemented and tested because a production dataset can differ and the rule costs nothing.

---

## 11. ⚠ Two findings that change the plan

### 11.1 The item→price seed already exists (corrects an earlier plan claim)

The plan (Review v1 R-9) claimed the price seed “cannot be implemented from the lookup”. **That is wrong.** The **sales** item lookup row already carries the price:

```csharp
// ErpWeb.Core/Sales/SaSoService.cs ~L74-86 (same shape in SaDo / SaInvoice / SaCdn)
var items = await db.IvStockMasters.AsNoTracking()
    .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive)
    .Select(x => new SaSoItemLookupRow { ICode = x.ICode, ..., SellingUom = x.SellingUom,
                                         StdUom = x.StdUom, StdPackSize = x.StdPackSize,
                                         SellingPrice = x.SellingPrice, TaxGroup = x.TaxGroup, ... })
```
and all four transaction pages seed the line from it:

```csharp
Popup.UnitPrice = item.SellingPrice ?? 0m;   // SaSo / SaDo / SaInvoice / SaCdn .razor.cs
```

Only the *inventory* DTO (`IvStockMasterLookupRow`) lacks `SellingPrice`. So the price-seed plumbing exists; what does **not** exist is any declared price *basis* or any customer/price-list/FOC resolution.

### 11.2 The price **basis** is live behaviour, not a future concern

`IsInclusive` is supplied **per line by the request** (`SaSoLineRequest.IsInclusive` etc.; services reject mixed inclusivity within a document: `if (request.Lines.Any(x => x.IsInclusive != firstInclusive))`), and `SaInvoiceCalc` un-taxes an inclusive line (`/(1 + t)`).

Therefore, today: `IvStockMaster.SellingPrice` (a **tax-exclusive** column, `decimal(18,4)`) is assigned straight into `UnitPrice`, and if the line is inclusive the engine treats that number as **tax-inclusive**. The plan’s §7.1 decision (exclusive basis + gross-up at the boundary) is correct, but it is a **behaviour change at the consumer boundary** and must be handled explicitly.

**Decision required (owner):**
- **(a) defer** — v2 leaves `SaSo/SaDo/SaInvoice/SaCdn` untouched; the contract (§8.9) records that the consumer must gross up when `IsInclusive`; no behaviour changes now. *(recommended: keeps this phase additive and review-free)*
- **(b) fix now** — touch the four transaction pages so a master price is converted to the line basis at seed time; requires regression testing of four shipped documents and is therefore a separate change with its own plan.

Also note: `?? 0m` means a missing item price currently seeds **0**, which contradicts the plan’s “never 0, block instead” rule. That rule belongs to the same consumer change, not to the master build.

---

## 12. Delete / reference dependency matrix

| Master | Referenced by | DB FK | App check | Delete |
|---|---|---|---|---|
| `IvCustPriceGroup` | `IvCustPrice` (child), `SaCust.CustPriceCode` | none (create as app check) | yes | **Block** while lines exist or a customer references it |
| `IvCustPrice` | nothing yet | none | yes | Allow (line removal is the intended path) |
| `SaItemCust` | nothing yet | none | yes (`Ok(message)` note) | Allow, with the “no consumer yet” note |
| `SaDisGroupItem` | nothing yet | none | yes (`Ok(message)` note) | Allow, with the “no consumer yet” note |
| `IvStockMaster` | referenced **by** the four new tables (validation only) | none | yes | unchanged (existing behaviour) |
| `SaDisGroup` / `SaDisCust` | `SaCust.GroupDiscount` (already blocked by shipped code) | none | yes | unchanged |

**No database cascade delete** is introduced. `DeleteCheckResult.Ok(message)` is used for the two consumer-less masters so “no references” is an explicit statement, not a silent `true`.

## 13. Writer inventory (who can write these tables)

| Object | Writers in this repo | Outside this repo |
|---|---|---|
| `IvCustPriceGroup`, `IvCustPrice`, `SaDisGroupItem` | **none** (absent from the model) | none expected (tables absent live) |
| `SaItemCust` | **none** — no entity, no service, no script | **unknown**; the table exists with no origin in this repo, so the production DB must be re-checked for legacy writers (WebForms `CustomerItems`/`ImportCustProduct`, mobile `DataService`) |
| `SaDisGroup`, `SaDisCust` | the shipped v1 Blazor masters | legacy only |

## 14. Script inventory

- `scripts/init-sales-masters.sql` creates `SaDisGroup` (PK `CompanyCode, GroupName, PayCode`) — v1.
- **Nothing** creates `IvCustPriceGroup`, `IvCustPrice`, `SaItemCust`, or `SaDisGroupItem`.
- ⇒ Phase 1 ships `scripts/init-sales-item-family.sql`, **alter-first for `SaItemCust`** (it exists, unmodelled, unscripted) and create-first for the three absent tables.

---

## 15. Open owner decisions created by these findings

| # | Decision | Recommended default |
|---|---|---|
| O1 | Price basis change: defer to the consumer phase, or fix the four transaction pages now | **defer (a)** — recorded in §8.9 of the plan |
| O2 | Carry `SaItemCust.CustModel` (nvarchar(10), not in the legacy spec) | carry as a mapped, UI-hidden column so no writer/value is lost |
| O3 | Carry `SaItemCust.DG` / `SG` (strings read as int elsewhere) | carry as-is, UI-hidden; never parsed in this phase |
| O4 | Carry the legacy `IvCustPriceGroup.Editable` flag | **drop** (table absent; no documented consumer) |
| O5 | Carry `SaDisGroupItem.GroupStatus` | carry, not authoritative, not exposed |
| O6 | `SaItemCust` key widening | **Branch A** (0 rows ⇒ drop/recreate PK with `CompanyCode`) |

## 16. Result

**PASS.** No STOP item was found: no incompatible-type divergence on a populated table, no duplicate business keys, no invalid tenant data, no unresolved item authority, and no dealer-price source to reconcile. The remaining work before Phase 1 is the **owner signature** on the decision register plus the six open decisions above.
