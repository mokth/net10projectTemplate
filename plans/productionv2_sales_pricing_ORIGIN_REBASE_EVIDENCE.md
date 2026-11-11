# Sales Pricing Phase A — applied onto current origin

**Date:** 11 November 2026  
**Worktree:** `C:\wincom\productionv2-sales-pricing`  
**Branch:** `sales-pricing-phase-a` (tracks `origin/productionv2`)  
**HEAD:** `584411e317ac6990ed8a5f0886207779d821deaf`  
**Working tree:** G1–G5 sales-pricing diff applied, **uncommitted**.

The operator checkout `c:\wincom\productionv2` was left on `e24f540` with mixed uncommitted Quality + Sales pricing. This worktree isolates **only** the sales-pricing files so they sit on current origin (14 WAC/FIFO replay commits) without touching Quality work.

## Apply

- Core/UI pricing patch applied cleanly (origin did not change those Sales production files).
- `SaInvoiceServiceTests.cs` / `SaDoServiceTests.cs` applied with a 42-line offset; **no conflict markers**.
- New files copied: `SaLinePricingTrigger.cs`, `SaInvoiceFromDeliveryOrderTests.cs`, `SaLinePricingTriggerTests.cs`.

## Tests (Sqlite)

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaCdnServiceTests|FullyQualifiedName~SaPriceInfoPresentationTests|FullyQualifiedName~SaLinePricingConsumerTests|FullyQualifiedName~SaItemFamilyPricingContractTests|FullyQualifiedName~SaCompanyPriceMethodTests|FullyQualifiedName~SaItemFamilyResolutionServiceTests|FullyQualifiedName~SaLinePricingTriggerTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~SaInvoiceServiceTests|FullyQualifiedName~SaDoServiceTests"
```

**679 passed, 0 failed, 0 skipped.** ~2m 17s. Includes origin Invoice/DO backdated WAC receipt replay tests plus the Phase A pricing filter.

Isolated SQL Server P01–P08: still **UNVERIFIED**.  
Browser UAT DO copy / CN amount2: still **not live** (DEMO has no posted DOs / no amount2 invoices). No stock was posted to manufacture those cases.

## Not done

- No commit (not requested).
- Operator `productionv2` checkout not rebased (Quality dirty files remain there).
- A6 / Phase B still blocked / unauthorized.
