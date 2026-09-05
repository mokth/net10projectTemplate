# Sales Master Phase 0 Findings

**Database:** `ERPWeb` on `.\SQLEXPRESS`  
**Audit date:** 2026-09-02  
**Phase 0 result:** **PASS**

## Summary

| Table | Exists in live DB | Data scope | Concurrency | Status |
|-------|-------------------|------------|-------------|--------|
| SaCustType | Yes | Company | Level A | CONFIRMED |
| SaCustGroup | Yes | Company | Level A | CONFIRMED |
| IvAreaCode | No (create via script) | Company | Level B | CONFIRMED |
| SaCountry | No (create via script) | Global | Level B | CONFIRMED |
| SaCurrency | No (create via script) | Company | Level B | CONFIRMED |
| SaDisGroup | No (create via script) | Company | Level B | CONFIRMED |
| SaDisCust | No (create via script) | Company (child) | Level B | CONFIRMED |
| SaCurrRate | No (create via script) | Global | Level B | CONFIRMED |

Missing tables are created by [scripts/init-sales-masters.sql](../../scripts/init-sales-masters.sql), aligned to live `SaCustType` audit/rowversion conventions where applicable. **CONFIRMED**

---

## SaCustType — CONFIRMED

- **PK:** `CompanyCode`, `CustTypeCode`
- **Unique indexes:** PK only
- **FKs:** None to/from SaCust (app check only)
- **Data scope:** Company
- **Security:** Company menu `SA_CUST_TYPE`
- **Concurrency:** Level A (`RowVersion` timestamp)
- **Audit:** Created, UserID, Updated, UpdatedUID (datetime2 / nvarchar(20))
- **Active:** `Active` bit NOT NULL, default true — boolean toggle
- **Branch/Location:** nullable columns; **leftover stamp on create** — CONFIRMED (matches MsUOM/SaCust pattern)
- **Column sizes (live):** CompanyCode nvarchar(10), CustTypeCode nvarchar(40), CustTypeDesc nvarchar(200)

---

## SaCustGroup — CONFIRMED

- **PK:** `CompanyCode`, `CustGroupCode`
- **Unique indexes:** PK only
- **FKs:** None (app check only)
- **Data scope:** Company
- **Security:** Company menu `SA_CUST_GROUP`
- **Concurrency:** Level A
- **Active:** No Active column — no SetActive toolbar
- **Branch/Location:** leftover stamp on create — CONFIRMED
- **Column sizes:** same as SaCustType pattern

---

## IvAreaCode — CONFIRMED (post init script)

- **PK:** `CompanyCode`, `AreaCode`
- **Data scope:** Company
- **Concurrency:** Level B (no RowVersion in legacy scaffold; script adds none)
- **Active:** None
- **Branch/Location:** leftover stamp on create — CONFIRMED
- **Lat/Long:** nvarchar(50) per legacy column names `latitude`, `longitude`

---

## SaCountry — CONFIRMED (post init script)

- **PK:** `CountryCode`
- **Data scope:** Global (no CompanyCode)
- **Security:** Company menu; **global-master authorization:** any company admin with menu ADD/EDIT/DELETE may maintain shared Country rows (same as legacy ERP pattern; no separate global-admin permission) — **CONFIRMED**
- **Concurrency:** Level B
- **Active:** None
- **Lat/Long:** decimal(9,6) nullable

---

## SaCurrency — CONFIRMED (post init script)

- **PK:** `CompanyCode`, `CurrCode`
- **Data scope:** Company
- **Concurrency:** Level B
- **Active:** `Active` bit nullable — treat null as active for assignment
- **Branch/Location:** leftover stamp on create — CONFIRMED

---

## SaDisGroup — CONFIRMED (post init script)

- **PK:** `CompanyCode`, `GroupName`, `PayCode`
- **Data scope:** Company
- **Concurrency:** Level B on header; aggregate membership sync in one transaction
- **DiscountType / GroupStatus / GroupLevel:** free text / numeric — no code table in DB; max lengths from script; no enum invented — **CONFIRMED**
- **Discount numeric:** float (legacy SQL `float`) — **CONFIRMED**
- **Branch/Location:** leftover stamp on create — CONFIRMED

---

## SaDisCust — CONFIRMED (post init script)

- **PK:** `CompanyCode`, `GroupName`, `PayCode`, `CustCode`
- **Parent identity:** full `SaDisGroup` key + CustCode
- **RowVersion:** None — aggregate-level save in one transaction; header RowVersion not on group — **Level B, last-writer-wins on concurrent membership** — CONFIRMED
- **Unique:** PK prevents duplicate membership

---

## SaCurrRate — CONFIRMED (post init script)

- **PK:** `SDate`, `EDate`, `CurrCode` (no CompanyCode — global rates)
- **Data scope:** Global
- **Concurrency:** Level B with transaction + lock on `SaCurrency` parent row (`UPDLOCK, HOLDLOCK`)
- **Dates:** datetime2, date-only semantics (time 00:00:00), **inclusive** start/end
- **EDate:** NOT NULL in schema — mandatory end date; no NULL open-ended rule
- **HomeCurPerUnit:** float, must be > 0
- **Status:** nvarchar(20) free text
- **Overlap index:** `IX_SaCurrRate_CurrCode_Dates` on `(CurrCode, SDate, EDate)`

---

## Delete dependency matrix

| Master | Referenced by | DB FK | App check | Delete |
|--------|---------------|-------|-----------|--------|
| SaCustType | SaCust.CustType | No | Yes | Block |
| SaCustGroup | SaCust.CustGroupCode | No | Yes | Block |
| IvAreaCode | SaCust.AreaCode | No | Yes | Block |
| SaCountry | SaCust.Country, ShipCountry, InvCountry, SaCustAdd.Country | No | Yes | Block |
| SaCurrency | SaCust.Currency, SaCurrRate.CurrCode | No | Yes | Block |
| SaDisGroup | SaCust.GroupDiscount, SaDisCust | No | Yes | Block |
| SaCurrRate | (none wired) | No | Yes | Allow if no refs |

---

## SaCust reference representation — CONFIRMED

| Field | Expected key | Live data (0 rows) | Clean | Fallback |
|-------|--------------|-------------------|-------|------------|
| CustType | CustTypeCode | N/A | Yes | Code combo |
| CustGroupCode | CustGroupCode | N/A | Yes | Code combo |
| AreaCode | AreaCode | N/A | Yes | Code combo |
| Country | CountryCode | N/A | Yes | Code combo (bind code) |
| Currency | CurrCode | N/A | Yes | Code combo |
| GroupDiscount | GroupName | N/A | Yes | GroupName combo |

Empty-list: Type/Group keep legacy bypass — **CONFIRMED**. New masters fail closed — **CONFIRMED**.

---

## Empty lookup rules — CONFIRMED

- **Type / Group:** legacy empty-list bypass for assignment validation
- **Area, Country, Currency, GroupDiscount:** fail closed when assignment list empty

---

## Open questions

None unresolved. **Phase 0 PASS.**
