---
name: E-Invoice Study Doc
overview: Write a detailed study markdown at `C:\wincom\net10projects\docs\e-invoice-study.md` covering Sales Invoice e-invoice flows (E-Submit, E-Status, E-Cancel) plus the full mapping from SalesInvoice header/details into the lib submission object (`DocumentHeader` / `PartyInfo` / `DocumentDetail`) and how that becomes the LHDN payload.
todos:
  - id: ensure-docs-dir
    content: Create C:\wincom\net10projects\docs if missing
    status: pending
  - id: write-study-md
    content: Write e-invoice-study.md with flows + full DocumentHeader/Detail mapping tables
    status: pending
isProject: false
---

# E-Invoice Study Markdown

## Deliverable

Create file: [`C:\wincom\net10projects\docs\e-invoice-study.md`](C:\wincom\net10projects\docs\e-invoice-study.md)

Create parent folder `C:\wincom\net10projects\docs\` if missing. Content is study/reference only (no MPOS code changes).

## Document structure

### 1. Architecture & layers
- UI [`InvoiceView.razor.cs`](MPosERP.Dev.UI/Sales/InvoiceView.razor.cs)
- Business [`InvoiceRepository`](MPosERP.Business/Repository/Sales/InvoiceRepository.cs) (`GetEInvDocument`, `SubmitEInvoice`, status/cancel)
- Lib [`SubmitDocumentHelper`](MPosERP.eInvoiceLib/SubmitDoc/SubmitDocumentHelper.cs) → [`GenerateInvoice`](MPosERP.eInvoiceLib/GenerateDoc/GenerateInvoice.cs) → [`E_InvoiceRepository`](MPosERP.eInvoiceLib/Repository/E_InvoiceRepository.cs)
- Mermaid flow: Invoice → DocumentHeader → UBL Root → Base64 SubmitDocument → LHDN

### 2. IRBM fields & status state machine
- Fields on `SalesInvoice`: `IsEInvoice`, `IRBMSubmitID`, `IRBMUUID`, `IRBMStatus`, dates, LongID, Error
- States: empty → Submitted → Valid / Invalid → Cancelled; submit allowed only empty or Invalid

### 3. E-Submit / E-Status / E-Cancel (UI + repo + API)
- UI guards and button modes (`e-submit`, `e-status`, `e-cancel`)
- Post auto-submit via `IsEInvoice()` + `errorCode == "EInvoice"`
- Repo methods and LHDN endpoints
- Quirks: batch E-Status, PostDoc double-submit path, UnPost does not cancel LHDN

### 4. Object population for submission (new section you asked for)

Source method: `InvoiceRepository.GetEInvDocument(SalesInvoice)` → returns `DocumentHeader` (lib input model in [`DocumentHeader.cs`](MPosERP.eInvoiceLib/Model/InputData/DocumentHeader.cs)).

Then lib:

```
DocumentHeader
  → GenerateInvoice.Generate() → Root / InvoiceJson (UBL JSON)
  → optional sign (v1.1)
  → Documents { codeNumber, format=JSON, document=base64, documentHash }
  → SubmitDocument.documents[]
  → POST /api/v1.0/documentsubmissions/
```

#### Header mapping (`SalesInvoice` + settings → `DocumentHeader`)

| DocumentHeader | Source |
|---|---|
| `IssueDate` | `DateTime.Now` (submit time, not invoice date) |
| `InvoicePeriodStartDate` / `EndDate` | `DocumentDate` as `yyyy-MM-dd` |
| `Currency` | hardcoded `"MYR"` |
| `DocumentNo` | `SalesInvoice.DocumentNo` |
| `RefDocumentNo` | `SalesInvoice.DocumentNo` |
| `TaxAmount` | `0` |
| `AmountExlTax`, `AmountIncTax`, `TotalNetAmount`, `TotalPayableAmount` | `SalesInvoice.TotalAmt` |
| `DiscountAmount` | `SalesInvoice.TotalDiscountAmt` |
| `docType` | not set explicitly (defaults; GenerateInvoice forces InvoiceTypeCode `"01"`) |
| `DetailTaxs` | empty list |
| `documentDetails` | built from non-deleted DTL rows |

#### Supplier (`PartyInfo`) from system settings + DTL settings

| PartyInfo | Source |
|---|---|
| CompanyName, Addr1–4, PostalCode, Email, PhoneNo | `SystemInfoAndSettings` |
| CityName / CountryCode / StateCode | SubMaster by SM_City/Country/State; defaults KL / MYS / 14 |
| TinNo | setting `EInvTIN` |
| RegNo | `RegisterNo` |
| RegType | setting `EInvBRNType` parse to `TINRegistrationType` |
| SSTNo | `"NA"` |
| IndustryClassificationCode / BizDesciption | MSIC code from `EInvIndustryClassificationCodeMSIC` |

#### Customer (`PartyInfo`) from customer master

| PartyInfo | Source |
|---|---|
| CompanyName, Addr1–4, PostalCode, Email, PhoneNo | Customer |
| City/Country/State | customer SubMasters; same defaults |
| TinNo / RegNo | Customer.TIN / RegisterNo |
| RegType | BRN default; map Customer.BRNType → NRIC / ARMY / PASSPORT |
| SSTNo | `"NA"` |

#### Line mapping (`StdDocumentEntryDTLInfo` → `DocumentDetail`)

Lines from `GetListSalesInvoiceDTLInfo(id)` where `IsDeleted == false`:

| DocumentDetail | Source / rule |
|---|---|
| `Line` | DTL `ID` as string |
| `Qty` | `Qty` |
| `UOM` | `EInvUOMType`, else `"H87"` |
| `UnitPrice` | `DecimalRound(LineAmt / LineQty, 2)` (overwrites raw UnitPrice) |
| `GrossAmount`, `AmountExclTax`, `AmountIncTax` | `LineAmt` |
| `DiscountAmount` | `LineDiscountAmt` |
| `TaxAmount` / `TaxPerCent` | `0` |
| `TaxType` | `"06"` (Not Applicable) |
| `ClassificationCode` | `EInvClassificationCode` → setting `EInvDefaultStockClassificationCode` → `"022"` |
| `ItemCode` / `ItemDesc` | `ShortCode` / `StockDescription` |

### 5. Port checklist
- Config keys, DI types, methods to reimplement, decision table for the three actions

## Implementation steps (after plan approval)

1. Ensure `C:\wincom\net10projects\docs\` exists
2. Write `e-invoice-study.md` with the sections above (complete mapping tables + flows)
3. No changes under `C:\MPOS`
