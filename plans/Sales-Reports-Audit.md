# Sales Module Reports & Inquiries — Technical Audit and Implementation Plan

## Executive Summary

This document provides a comprehensive technical audit of the existing ERP Malaysia Sales module and an implementation plan for 130 requested reports/inquiries. The audit examines the existing architecture, identifies reusable components, and classifies each report by implementation feasibility.

**Key Findings:**
- The Sales module has a robust entity model with complete document flow relationships
- Existing services provide sales analysis, document flow queries, and transaction management
- The UI architecture uses DevExpress components with consistent patterns
- Most "MUST" priority reports can be implemented with moderate effort
- Some "SHOULD" priority reports require new SQL views or business logic
- The existing document flow implementation can be extended for the Sales Document Flow Inquiry

---

## 1. Existing Architecture Analysis

### 1.1 Sales Document Entities

| Entity | Table | Primary Key | Document Number | Key Relationships |
|--------|-------|-------------|-----------------|-------------------|
| SaQt | SaQt | (CompanyCode, BranchCode, QtNo, CustRel) | QtNo | → SaSo (QtNo, QtCustRel) |
| SaSo | SaSo | (CompanyCode, BranchCode, SoNo, CustRel) | SoNo | ← SaQt, → SaDoDetail |
| SaDo | SaDo | (CompanyCode, BranchCode, DoNo) | DoNo | ← SaSoDetail, → SaInvoiceDetail |
| SaInvoice | SaInvoice | (CompanyCode, BranchCode, InvNo) | InvNo | ← SaDoDetail, → SaCdn |
| SaCdn | SaCdn | (CompanyCode, BranchCode, DocNo) | DocNo | ← SaInvoice (Type=CN/DN) |

### 1.2 Document Flow Relationships

**Primary Flow:**
```text
Quotation (SaQt) 
   ↓ [SaSo.QtNo, SaSo.QtCustRel]
Sales Order (SaSo)
   ↓ [SaDoDetail.SoNo, SaDoDetail.SoLine]
Delivery Order (SaDo)
   ↓ [SaInvoiceDetail.DoNo, SaInvoiceDetail.DoLine]
Sales Invoice (SaInvoice)
   ↓ [SaCdn.InvNo]
Credit/Debit Note (SaCdn)
```

**Document Flow Ledger:**
- `SaDocApplication` - Tracks allocation relationships between SO/DO/INV
- `SaDocFlowQuery` - Service for querying document flow
- `SaDocFlowPanel` - UI component for displaying document flow

### 1.3 Customer Entities

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| SaCust | Customer master | CustCode, CustName, CustGroupCode, SalesRep |
| SaCustAdd | Customer addresses | Address1-4, City, State, PostalCode, Country |
| SaCustContact | Customer contacts | ContactPerson, Tel, Fax, Email |
| SaCustType | Customer types | TypeCode, Description |
| SaCustGroup | Customer groups | GroupCode, Description |
| IvAreaCode | Area codes | AreaCode, Description |
| IvMsCode | Master codes (Source, Industry, Channel) | Code, Type, Description |

### 1.4 Existing Services

| Service | Purpose | Methods |
|---------|---------|---------|
| ISaSalesAnalysisService | Sales analysis queries | GetSalesSummaryAsync, GetSalesRepAttainmentAsync, GetQtConversionAsync |
| ISaDocFlowQuery | Document flow queries | QueryAsync |
| ISaQtService | Quotation CRUD | SaveNewAsync, SaveUpdateAsync, etc. |
| ISaSoService | Sales Order CRUD | SaveNewAsync, SaveUpdateAsync, etc. |
| ISaDoService | Delivery Order CRUD | SaveNewAsync, SaveUpdateAsync, etc. |
| ISaInvoiceService | Invoice CRUD | SaveNewAsync, SaveUpdateAsync, etc. |
| ISaCdnService | Credit/Debit Note CRUD | SaveNewAsync, SaveUpdateAsync, etc. |
| ISaCustService | Customer CRUD | SaveNewAsync, SaveUpdateAsync, etc. |
| ISaCustLookupService | Customer lookups | SearchCustomersAsync, ListSalesRepsForAssignmentAsync, etc. |

### 1.5 Existing UI Pages

| Page | Route | Purpose |
|------|-------|---------|
| SaSalesSummary | /sales/analysis/summary | Sales summary by dimension |
| SaSalesRepAttainment | /sales/analysis/attainment | Sales rep target vs actual |
| SaQtConversionAnalysis | /sales/analysis/qt-conversion | Quotation conversion analysis |
| SaQtList | /sales/qt/list | Quotation listing |
| SaSoList | /sales/so/list | Sales Order listing |
| SaDoList | /sales/do/list | Delivery Order listing |
| SaInvoiceList | /sales/invoice/list | Invoice listing |
| SaCdnList | /sales/cdn/list | Credit Note listing |
| SaCdnDebitList | /sales/cdn/debit-list | Debit Note listing |
| PriceInquiry | /sales/price-inquiry | Price inquiry |

### 1.6 Existing SQL Views

**No SQL views found** - All queries are implemented using LINQ/Entity Framework.

### 1.7 Existing Reports

| Report | Type | Status |
|--------|------|--------|
| Sales Summary | Analysis page | Implemented |
| Sales Rep Attainment | Analysis page | Implemented |
| Quotation Conversion | Analysis page | Implemented |
| Document Flow Panel | Embedded component | Implemented |
| Price Inquiry | Inquiry page | Implemented |

---

## 2. Audit Matrix

### Classification Key

- `READY` — existing functionality already provides it
- `REUSABLE` — can be implemented mainly using existing data/view/component
- `NEW VIEW REQUIRED` — database/SQL view required
- `NEW LOGIC REQUIRED` — business logic/service required
- `NEW UI REQUIRED` — new inquiry/report page required
- `NOT POSSIBLE YET` — required source data or relationship does not currently exist
- `DUPLICATE` — already covered by an existing report

### A. Customer Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 1 | Customer Profile | MUST | YES | SaCust, SaCustAdd, SaCustContact | No | No | Minor | Low | Enhance existing page |
| 2 | Customer Transaction Inquiry | MUST | PARTIAL | Multiple tables | Yes | Yes | Yes | Medium | Implement |
| 3 | Customer Sales History | MUST | PARTIAL | SaInvoice, SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 4 | Customer Statement | MUST | NO | SaInvoice, SaCdn, Payments | Yes | Yes | Yes | High | Implement |
| 5 | Customer Outstanding | MUST | PARTIAL | SaInvoice, Payments | Yes | Yes | Yes | Medium | Implement |
| 6 | Customer Credit Limit Inquiry | MUST | NO | SaCust, SaInvoice | Yes | Yes | Yes | Medium | Implement |
| 7 | Customer Sales Trend | SHOULD | NO | SaInvoice | Yes | Yes | Yes | Medium | Implement |
| 8 | Customer Purchase Frequency | SHOULD | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| 9 | Customer Average Order Value | SHOULD | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| 10 | Customer Return Analysis | SHOULD | NO | SaCdn | Yes | Yes | Yes | Medium | Implement |
| 11 | Customer Profitability | SHOULD | NO | SaInvoice, Cost data | Yes | Yes | Yes | High | Implement |

### B. Quotation Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 12 | Quotation Listing | MUST | YES | SaQt | No | No | No | Low | Already exists |
| 13 | Quotation Detail | MUST | YES | SaQt, SaQtDetail | No | No | No | Low | Already exists |
| 14 | Quotation Status Inquiry | MUST | PARTIAL | SaQt | Yes | Yes | Yes | Medium | Implement |
| 15 | Quotation Conversion Inquiry | MUST | YES | SaQt, SaSo | No | No | No | Low | Already exists |
| 16 | Quotation Expiry Inquiry | MUST | NO | SaQt | Yes | Yes | Yes | Low | Implement |
| 17 | Quotation Revision History | MUST | YES | SaQt | No | No | No | Low | Already exists |
| 18 | Quotation by Customer | MUST | NO | SaQt | Yes | Yes | Yes | Low | Implement |
| 19 | Quotation by Salesperson | MUST | NO | SaQt | Yes | Yes | Yes | Low | Implement |
| 20 | Quotation Win/Loss Analysis | SHOULD | YES | SaQt | No | No | No | Low | Already exists |
| 21 | Quotation Conversion Rate | SHOULD | YES | SaQt, SaSo | No | No | No | Low | Already exists |
| 22 | Quotation Aging | SHOULD | NO | SaQt | Yes | Yes | Yes | Low | Implement |
| 23 | Quotation Trend | SHOULD | NO | SaQt | Yes | Yes | Yes | Medium | Implement |

### C. Sales Order Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 24 | Sales Order Listing | MUST | YES | SaSo | No | No | No | Low | Already exists |
| 25 | Open Sales Order | MUST | PARTIAL | SaSo | Yes | Yes | Yes | Low | Implement |
| 26 | Sales Order Outstanding Quantity | MUST | NO | SaSo, SaSoDetail | Yes | Yes | Yes | Medium | Implement |
| 27 | Sales Order Status | MUST | PARTIAL | SaSo | Yes | Yes | Yes | Low | Implement |
| 28 | Sales Order by Customer | MUST | NO | SaSo | Yes | Yes | Yes | Low | Implement |
| 29 | Sales Order by Salesperson | MUST | NO | SaSo | Yes | Yes | Yes | Low | Implement |
| 30 | Sales Order by Item | MUST | NO | SaSoDetail | Yes | Yes | Yes | Medium | Implement |
| 31 | Sales Order Delivery Status | MUST | NO | SaSo, SaDoDetail | Yes | Yes | Yes | Medium | Implement |
| 32 | Sales Order Invoice Status | MUST | NO | SaSo, SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 33 | Backorder Report | SHOULD | NO | SaSo, SaSoDetail | Yes | Yes | Yes | Medium | Implement |
| 34 | Sales Order Aging | SHOULD | NO | SaSo | Yes | Yes | Yes | Low | Implement |
| 35 | Expected Delivery Analysis | SHOULD | NO | SaSo, SaDo | Yes | Yes | Yes | Medium | Implement |
| 36 | Cancelled Sales Order Analysis | SHOULD | NO | SaSo | Yes | Yes | Yes | Low | Implement |
| 37 | Sales Order Fulfillment Rate | SHOULD | NO | SaSo, SaDoDetail | Yes | Yes | Yes | Medium | Implement |

### D. Delivery Order Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 38 | Delivery Order Listing | MUST | YES | SaDo | No | No | No | Low | Already exists |
| 39 | Open Delivery Order | MUST | PARTIAL | SaDo | Yes | Yes | Yes | Low | Implement |
| 40 | Delivery Order by Customer | MUST | NO | SaDo | Yes | Yes | Yes | Low | Implement |
| 41 | Delivery Order by Item | MUST | NO | SaDoDetail | Yes | Yes | Yes | Medium | Implement |
| 42 | SO vs DO Inquiry | MUST | NO | SaSo, SaDoDetail | Yes | Yes | Yes | Medium | Implement |
| 43 | DO vs Invoice Inquiry | MUST | NO | SaDo, SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 44 | Pending Invoice from DO | MUST | NO | SaDo, SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 45 | Delivery Status Inquiry | MUST | PARTIAL | SaDo | Yes | Yes | Yes | Low | Implement |
| 46 | Delivery Aging | SHOULD | NO | SaDo | Yes | Yes | Yes | Low | Implement |
| 47 | Delivery by Warehouse | SHOULD | NO | SaDo | Yes | Yes | Yes | Low | Implement |
| 48 | Delivery by Salesperson | SHOULD | NO | SaDo | Yes | Yes | Yes | Low | Implement |
| 49 | Partial Delivery Analysis | SHOULD | NO | SaSo, SaDoDetail | Yes | Yes | Yes | Medium | Implement |
| 50 | Delivery Performance | SHOULD | NO | SaDo, SaSo | Yes | Yes | Yes | Medium | Implement |

### E. Sales Invoice Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 51 | Sales Invoice Listing | MUST | YES | SaInvoice | No | No | No | Low | Already exists |
| 52 | Sales Invoice Detail | MUST | YES | SaInvoice, SaInvoiceDetail | No | No | No | Low | Already exists |
| 53 | Sales by Customer | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 54 | Sales by Item | MUST | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 55 | Sales by Salesperson | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 56 | Sales by Date | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 57 | Sales by Warehouse | MUST | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 58 | Sales by Category | MUST | NO | SaInvoiceDetail, IvStockMaster | Yes | Yes | Yes | Medium | Implement |
| 59 | Invoice Outstanding | MUST | NO | SaInvoice, Payments | Yes | Yes | Yes | Medium | Implement |
| 60 | Invoice Aging | MUST | NO | SaInvoice | Yes | Yes | Yes | Medium | Implement |
| 61 | Customer Statement | MUST | NO | SaInvoice, SaCdn, Payments | Yes | Yes | Yes | High | Implement |
| 62 | Invoice Payment Status | MUST | NO | SaInvoice, Payments | Yes | Yes | Yes | Medium | Implement |
| 63 | Invoice Cancellation/Void | MUST | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| 64 | Invoice vs DO | MUST | NO | SaInvoice, SaDoDetail | Yes | Yes | Yes | Medium | Implement |
| 65 | Invoice vs SO | MUST | NO | SaInvoice, SaSo | Yes | Yes | Yes | Medium | Implement |
| 66 | Gross Sales Analysis | SHOULD | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| 67 | Net Sales Analysis | SHOULD | NO | SaInvoice, SaCdn | Yes | Yes | Yes | Medium | Implement |
| 68 | Discount Analysis | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 69 | Sales Return Analysis | SHOULD | NO | SaCdn | Yes | Yes | Yes | Medium | Implement |
| 70 | SST/Tax Analysis | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 71 | Gross Margin Analysis | SHOULD | NO | SaInvoiceDetail, Cost data | Yes | Yes | Yes | High | Implement |
| 72 | Average Selling Price | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Low | Implement |
| 73 | Customer Sales Ranking | SHOULD | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 74 | Item Sales Ranking | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 75 | Monthly Sales Trend | SHOULD | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |

### F. Credit Note Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 76 | Credit Note Listing | MUST | YES | SaCdn (Type=CN) | No | No | No | Low | Already exists |
| 77 | Credit Note Detail | MUST | YES | SaCdn, SaCdnDetail | No | No | No | Low | Already exists |
| 78 | Credit Note by Customer | MUST | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 79 | Credit Note by Reason | MUST | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 80 | Credit Note vs Invoice | MUST | NO | SaCdn, SaInvoice | Yes | Yes | Yes | Medium | Implement |
| 81 | Credit Note Sales Impact | MUST | NO | SaCdn, SaInvoice | Yes | Yes | Yes | Medium | Implement |
| 82 | Credit Note Applied/Outstanding | MUST | NO | SaCdn, Payments | Yes | Yes | Yes | Medium | Implement |
| 83 | Credit Note Trend | SHOULD | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 84 | Customer Return Analysis | SHOULD | NO | SaCdn | Yes | Yes | Yes | Medium | Implement |
| 85 | Item Return Analysis | SHOULD | NO | SaCdnDetail | Yes | Yes | Yes | Medium | Implement |
| 86 | Return Reason Analysis | SHOULD | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 87 | Credit Note % of Sales | SHOULD | NO | SaCdn, SaInvoice | Yes | Yes | Yes | Medium | Implement |

### G. Debit Note Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 88 | Debit Note Listing | MUST | YES | SaCdn (Type=DN) | No | No | No | Low | Already exists |
| 89 | Debit Note Detail | MUST | YES | SaCdn, SaCdnDetail | No | No | No | Low | Already exists |
| 90 | Debit Note by Customer | MUST | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 91 | Debit Note vs Invoice | MUST | NO | SaCdn, SaInvoice | Yes | Yes | Yes | Medium | Implement |
| 92 | Debit Note Reason Analysis | MUST | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 93 | Debit Note Applied/Outstanding | MUST | NO | SaCdn, Payments | Yes | Yes | Yes | Medium | Implement |
| 94 | Debit Note Trend | SHOULD | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| 95 | Debit Note by Customer | SHOULD | NO | SaCdn | Yes | Yes | Yes | Low | Duplicate of #90 |
| 96 | Debit Note by Reason | SHOULD | NO | SaCdn | Yes | Yes | Yes | Low | Duplicate of #92 |
| 97 | Debit Note % of Sales | SHOULD | NO | SaCdn, SaInvoice | Yes | Yes | Yes | Medium | Implement |

### H. Malaysia e-Invoice Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 98 | e-Invoice Submission Inquiry | MUST | NO | EInvDocSubmission, SaEInvoiceLog | Yes | Yes | Yes | Medium | Implement |
| 99 | e-Invoice Status Inquiry | MUST | PARTIAL | SaInvoice, SaCdn | Yes | Yes | Yes | Medium | Implement |
| 100 | e-Invoice Validation Result | MUST | NO | SaEInvoiceLog | Yes | Yes | Yes | Medium | Implement |
| 101 | e-Invoice Error/Exception Report | MUST | NO | SaEInvoiceLog | Yes | Yes | Yes | Medium | Implement |
| 102 | e-Invoice UUID Inquiry | MUST | NO | EInvDocSubmission | Yes | Yes | Yes | Low | Implement |
| 103 | e-Invoice Document Type Inquiry | MUST | NO | EInvDocSubmission | Yes | Yes | Yes | Low | Implement |
| 104 | e-Invoice Submission History | MUST | NO | EInvDocSubmission | Yes | Yes | Yes | Medium | Implement |
| 105 | e-Invoice Resubmission History | MUST | NO | SaEInvoiceLog | Yes | Yes | Yes | Medium | Implement |
| 106 | e-Invoice Cancellation Inquiry | MUST | NO | EInvDocSubmission | Yes | Yes | Yes | Low | Implement |
| 107 | e-Invoice by Customer | MUST | NO | EInvDocSubmission | Yes | Yes | Yes | Low | Implement |
| 108 | e-Invoice by Date | MUST | NO | EInvDocSubmission | Yes | Yes | Yes | Low | Implement |
| 109 | e-Invoice Reconciliation | MUST | NO | SaInvoice, EInvDocSubmission | Yes | Yes | Yes | High | Implement |
| 110 | e-Invoice Processing Dashboard | SHOULD | NO | Multiple | Yes | Yes | Yes | High | Implement |
| 111 | Failed e-Invoice Analysis | SHOULD | NO | SaEInvoiceLog | Yes | Yes | Yes | Medium | Implement |
| 112 | e-Invoice Submission Trend | SHOULD | NO | EInvDocSubmission | Yes | Yes | Yes | Medium | Implement |
| 113 | e-Invoice Exception Aging | SHOULD | NO | SaEInvoiceLog | Yes | Yes | Yes | Medium | Implement |

### I. Sales Analysis Reports

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| 114 | Sales by Customer | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 115 | Sales by Item | MUST | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 116 | Sales by Category | MUST | NO | SaInvoiceDetail, IvStockMaster | Yes | Yes | Yes | Medium | Implement |
| 117 | Sales by Salesperson | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 118 | Sales by Warehouse | MUST | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 119 | Sales Trend | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 120 | Sales Summary | MUST | YES | SaInvoice | No | No | No | Low | Already exists |
| 121 | Customer Sales Ranking | SHOULD | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| 122 | Item Sales Ranking | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 123 | Salesperson Performance | SHOULD | YES | SaInvoice, SaSalesRepTarget | No | No | No | Low | Already exists (Attainment) |
| 124 | Sales Target vs Actual | SHOULD | YES | SaInvoice, SaSalesRepTarget | No | No | No | Low | Already exists (Attainment) |
| 125 | Gross Margin | SHOULD | NO | SaInvoiceDetail, Cost data | Yes | Yes | Yes | High | Implement |
| 126 | Discount Analysis | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 127 | Price Analysis | SHOULD | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| 128 | Customer Profitability | SHOULD | NO | SaInvoice, Cost data | Yes | Yes | Yes | High | Implement |
| 129 | Product Contribution | SHOULD | NO | SaInvoiceDetail, Cost data | Yes | Yes | Yes | High | Implement |
| 130 | Sales Return Analysis | SHOULD | NO | SaCdn | Yes | Yes | Yes | Medium | Implement |

### J. Sales Dashboard

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| KPIs | Sales Today | MUST | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| KPIs | Sales This Month | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| KPIs | Sales This Year | MUST | YES | SaInvoice | No | No | No | Low | Already exists (Sales Summary) |
| KPIs | Open Quotation | MUST | NO | SaQt | Yes | Yes | Yes | Low | Implement |
| KPIs | Open Sales Order | MUST | NO | SaSo | Yes | Yes | Yes | Low | Implement |
| KPIs | Pending Delivery | MUST | NO | SaSo, SaDo | Yes | Yes | Yes | Low | Implement |
| KPIs | Outstanding Invoice | MUST | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| KPIs | Overdue Invoice | MUST | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| KPIs | Credit Note | MUST | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| KPIs | Debit Note | MUST | NO | SaCdn | Yes | Yes | Yes | Low | Implement |
| Charts | Monthly Sales Trend | MUST | YES | SaInvoice | No | No | No | Low | Already exists |
| Charts | Sales by Customer | MUST | YES | SaInvoice | No | No | No | Low | Already exists |
| Charts | Sales by Product Category | MUST | NO | SaInvoiceDetail, IvStockMaster | Yes | Yes | Yes | Medium | Implement |
| Charts | Sales by Salesperson | MUST | YES | SaInvoice | No | No | No | Low | Already exists |
| Charts | Top Customers | MUST | NO | SaInvoice | Yes | Yes | Yes | Low | Implement |
| Charts | Top Items | MUST | NO | SaInvoiceDetail | Yes | Yes | Yes | Medium | Implement |
| Charts | Outstanding AR | MUST | NO | SaInvoice, Payments | Yes | Yes | Yes | Medium | Implement |
| Charts | Overdue AR | MUST | NO | SaInvoice, Payments | Yes | Yes | Yes | Medium | Implement |
| Charts | Quotation Conversion | MUST | YES | SaQt, SaSo | No | No | No | Low | Already exists |

### K. Sales Document Flow Inquiry

| # | Report | Priority | Existing? | Data Source | SQL View | New Logic | New UI | Complexity | Recommendation |
|---|--------|----------|-----------|------------|----------|-----------|--------|------------|----------------|
| Flow | Sales Document Flow Inquiry | MUST | YES | SaDocApplication, SaQt, SaSo, SaDo, SaInvoice, SaCdn | No | No | Minor | Low | Enhance existing |

---

## 3. Implementation Summary

### 3.1 Statistics

| Category | Total | READY | REUSABLE | NEW VIEW | NEW LOGIC | NEW UI | NOT POSSIBLE | DUPLICATE |
|----------|-------|-------|----------|----------|-----------|--------|--------------|-----------|
| Customer | 11 | 1 | 0 | 10 | 10 | 10 | 0 | 0 |
| Quotation | 12 | 6 | 0 | 6 | 6 | 6 | 0 | 0 |
| Sales Order | 14 | 1 | 0 | 13 | 13 | 13 | 0 | 0 |
| Delivery Order | 13 | 1 | 0 | 12 | 12 | 12 | 0 | 0 |
| Sales Invoice | 25 | 6 | 0 | 19 | 19 | 19 | 0 | 0 |
| Credit Note | 12 | 2 | 0 | 10 | 10 | 10 | 0 | 0 |
| Debit Note | 10 | 2 | 0 | 8 | 8 | 8 | 0 | 2 |
| e-Invoice | 16 | 0 | 0 | 16 | 16 | 16 | 0 | 0 |
| Sales Analysis | 17 | 6 | 0 | 11 | 11 | 11 | 0 | 0 |
| Dashboard | 19 | 5 | 0 | 14 | 14 | 14 | 0 | 0 |
| Document Flow | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 0 |
| **Total** | **150** | **31** | **0** | **119** | **119** | **119** | **0** | **2** |

### 3.2 Key Findings

1. **Existing Coverage:** 31 reports (21%) are already implemented or can be enhanced with minor changes
2. **New Implementation Required:** 119 reports (79%) require new SQL views, business logic, and UI
3. **No SQL Views Exist:** All current queries use LINQ/Entity Framework
4. **Document Flow Exists:** The Sales Document Flow Inquiry can be enhanced from existing implementation
5. **e-Invoice Data Available:** All required e-Invoice data is stored in existing tables
6. **Accounting Integration Missing:** No direct GL/AR integration found for reconciliation reports

### 3.3 Dependencies and Risks

**Dependencies:**
1. Payment/AR data required for Customer Statement, Outstanding, Aging reports
2. Cost data required for Profitability and Margin analysis
3. GL integration required for Accounting reconciliation
4. Warehouse data required for Warehouse-based reports

**Risks:**
1. Performance: Large transaction tables may require server-side processing
2. Data Consistency: Multiple document types need consistent filtering
3. Multi-tenancy: All reports must respect Company/Branch filtering
4. Accounting Accuracy: Reports must reconcile with existing accounting data

---

## 4. Recommended Implementation Phases

### Phase 1: Core Operational Inquiries (4-6 weeks)

**Focus:** Essential operational reports for daily sales operations

**Reports to Implement:**
- Customer Transaction Inquiry (#2)
- Customer Sales History (#3)
- Customer Outstanding (#5)
- Quotation Status Inquiry (#14)
- Quotation Expiry Inquiry (#16)
- Open Sales Order (#25)
- Sales Order Outstanding Quantity (#26)
- Sales Order Delivery Status (#31)
- Sales Order Invoice Status (#32)
- Open Delivery Order (#39)
- SO vs DO Inquiry (#42)
- DO vs Invoice Inquiry (#43)
- Pending Invoice from DO (#44)
- Invoice Outstanding (#59)
- Invoice Aging (#60)
- Invoice Payment Status (#62)
- Credit Note by Customer (#78)
- Debit Note by Customer (#90)
- e-Invoice Status Inquiry (#99)
- e-Invoice Error/Exception Report (#101)
- Sales Document Flow Inquiry (enhance)

**Technical Requirements:**
1. Create base SQL views for common queries
2. Implement server-side filtering and pagination
3. Create reusable UI components for filters and grids
4. Ensure multi-tenant security

### Phase 2: Sales Analysis (3-4 weeks)

**Focus:** Sales performance analysis and reporting

**Reports to Implement:**
- Sales by Item (#54)
- Sales by Warehouse (#57)
- Sales by Category (#58)
- Customer Sales Trend (#7)
- Quotation by Customer (#18)
- Quotation by Salesperson (#19)
- Sales Order by Customer (#28)
- Sales Order by Salesperson (#29)
- Sales Order by Item (#30)
- Delivery Order by Customer (#40)
- Delivery Order by Item (#41)
- Delivery by Warehouse (#47)
- Delivery by Salesperson (#48)
- Item Sales Ranking (#122)
- Top Customers (Dashboard)
- Top Items (Dashboard)

**Technical Requirements:**
1. Extend existing Sales Summary service
2. Create item-level analysis views
3. Implement drill-down functionality
4. Add chart visualizations

### Phase 3: Management Dashboard (2-3 weeks)

**Focus:** Executive dashboard and KPIs

**Reports to Implement:**
- Sales Dashboard with all KPIs
- Monthly Sales Trend (enhance)
- Sales by Product Category (chart)
- Outstanding AR (chart)
- Overdue AR (chart)
- Open Quotation KPI
- Open Sales Order KPI
- Pending Delivery KPI
- Outstanding Invoice KPI
- Overdue Invoice KPI

**Technical Requirements:**
1. Create dashboard page with KPI cards
2. Implement chart components
3. Add real-time data refresh
4. Optimize for performance

### Phase 4: Advanced Analysis (4-6 weeks)

**Focus:** Advanced analytics and e-Invoice reporting

**Reports to Implement:**
- Customer Statement (#4)
- Customer Credit Limit Inquiry (#6)
- Customer Profitability (#11)
- Gross Margin Analysis (#71)
- e-Invoice Submission Inquiry (#98)
- e-Invoice Validation Result (#100)
- e-Invoice Reconciliation (#109)
- e-Invoice Processing Dashboard (#110)
- Credit Note vs Invoice (#80)
- Debit Note vs Invoice (#91)
- SST/Tax Analysis (#70)
- Discount Analysis (#68)
- Price Analysis (#127)

**Technical Requirements:**
1. Implement payment/AR integration
2. Create e-Invoice reporting service
3. Implement accounting reconciliation
4. Add advanced filtering and grouping

### Phase 5: Remaining Reports (2-3 weeks)

**Focus:** Complete remaining reports and enhancements

**Reports to Implement:**
- All remaining SHOULD priority reports
- Report enhancements and optimizations
- Performance tuning
- User feedback implementation

---

## 5. Technical Implementation Guidelines

### 5.1 SQL View Strategy

**Recommendation:** Create SQL views for complex queries that cannot be efficiently implemented in LINQ.

**View Categories:**
1. **Base Views:** Common joins and filters
2. **Aggregation Views:** Summary and ranking queries
3. **Analysis Views:** Complex analytical queries
4. **Reconciliation Views:** Accounting integration

**Example View Structure:**
```sql
-- Base view for sales transactions
CREATE VIEW vSalesTransactions AS
SELECT 
    inv.CompanyCode,
    inv.BranchCode,
    inv.InvNo,
    inv.InvDate,
    inv.CustCode,
    inv.TotAmnt,
    -- ... other fields
FROM SaInvoice inv
WHERE inv.Status = 'POSTED'

-- Aggregation view for sales by customer
CREATE VIEW vSalesByCustomer AS
SELECT 
    CompanyCode,
    BranchCode,
    CustCode,
    COUNT(*) AS InvoiceCount,
    SUM(TotAmnt) AS TotalAmount
FROM vSalesTransactions
GROUP BY CompanyCode, BranchCode, CustCode
```

### 5.2 Service Layer Pattern

**Follow existing pattern:**
```csharp
public interface ISalesReportService
{
    Task<IvMasterOperationResult<SalesReportResult>> GetReportAsync(
        SalesReportQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class SalesReportService : ISalesReportService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    
    // Implementation
}
```

### 5.3 UI Component Pattern

**Follow existing DevExpress pattern:**
```razor
@page "/sales/reports/report-name"
@inherits ReportPageBase

<PageTitle>Report Name</PageTitle>

<MenuAuthorize MenuCode="@MenuCodes.SalesReportName">
    <div class="iv-page">
        <!-- Filter section -->
        <section class="iv-card">
            <!-- Date range, customer, warehouse filters -->
        </section>
        
        <!-- Results section -->
        @if (Result is not null)
        {
            <section class="iv-card">
                <DxGrid Data="@Result.Rows">
                    <Columns>
                        <!-- Grid columns -->
                    </Columns>
                </DxGrid>
            </section>
        }
    </div>
</MenuAuthorize>
```

### 5.4 Performance Considerations

1. **Server-side Filtering:** Use `IQueryable` with proper filtering before materialization
2. **Pagination:** Implement skip/take pattern for large datasets
3. **Indexing:** Ensure proper indexes on filter columns
4. **Async Operations:** Use async/await throughout
5. **Caching:** Consider caching for frequently accessed data
6. **Batch Processing:** Use batch queries for multiple related data

### 5.5 Multi-Tenancy Security

**All reports must include:**
```csharp
var scope = _tenant.TryBranchScope();
if (scope is null) return IvMasterOperationResult<T>.Fail(...);

var company = scope.CompanyCode;
var branch = scope.BranchCode;

// Filter all queries by company and branch
```

---

## 6. Conclusion

The existing Sales module provides a solid foundation for implementing the requested reports. With 31 reports already implemented and 119 requiring new development, the implementation can be phased over 15-22 weeks.

**Key Success Factors:**
1. Follow existing architecture patterns
2. Implement proper SQL views for performance
3. Ensure multi-tenant security
4. Maintain accounting data integrity
5. Provide drill-down and navigation capabilities
6. Optimize for large datasets

**Next Steps:**
1. Approve implementation phases
2. Begin Phase 1 development
3. Establish testing and validation procedures
4. Plan user training and rollout

---

*Document Version: 1.0*
*Last Updated: 2026-09-26*
*Prepared by: AI Assistant*