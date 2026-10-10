using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Data;

public class AppDbContext : DbContext
{
    // The FG service owns this lifecycle; generic inventory services never set this capability.
    public bool FinishedGoodReceiptWrite { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateFinishedGoodWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateFinishedGoodWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    private void ValidateFinishedGoodWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.State != EntityState.Added
                && (entry.Entity is StockValuationFact
                    or StockValuationPeriodSnapshotHdr or StockValuationPeriodSnapshotLine
                    or PurchaseReceiptCostSettlement or PurchaseCostAdjustment
                    or SalesReturnCostAllocation or SalesReturnStandardCostVariance
                    or StockFifoLayerConsumption
                    or ProductionStandardCostVariance
                    or ProductionConversionCostFact))
                throw new InvalidOperationException("Valuation facts, financial snapshots, and costing evidence are immutable; append a linked reversal or a new revision.");
            if (entry.State != EntityState.Added && entry.Entity is ProductionFinishedGoodFact or ProductionFinishedGoodLotOrigin
                or ProductionFinishedGoodPriceSnapshot or ProductionValuationEvidence or ProductionPoolDependency)
                throw new InvalidOperationException("Posted evidence and FG lot origin are immutable; append a linked reversal instead.");
            if (!FinishedGoodReceiptWrite && (entry.Entity is ProductionFinishedGoodReceipt or ProductionFinishedGoodSource
                || entry.Entity is IvTrxBatch b && b.TrxType == "FG" || entry.Entity is IvTrxBatchDetail d && d.TrxType == "FG"))
                throw new InvalidOperationException("Finished Good Receipt documents must be changed through the FG service.");
        }
    }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProductionFinishedGoodReceipt> ProductionFinishedGoodReceiptRows => Set<ProductionFinishedGoodReceipt>();
    public DbSet<ProductionFinishedGoodSource> ProductionFinishedGoodSourceRows => Set<ProductionFinishedGoodSource>();
    public DbSet<ProductionFinishedGoodFact> ProductionFinishedGoodFactRows => Set<ProductionFinishedGoodFact>();
    public DbSet<ProductionFinishedGoodPriceSnapshot> ProductionFinishedGoodPriceSnapshotRows => Set<ProductionFinishedGoodPriceSnapshot>();
    public DbSet<ProductionFinishedGoodLotOrigin> ProductionFinishedGoodLotOriginRows => Set<ProductionFinishedGoodLotOrigin>();
    public DbSet<ProductionPoolValuation> ProductionPoolValuationRows => Set<ProductionPoolValuation>();
    public DbSet<ProductionValuationEvidence> ProductionValuationEvidenceRows => Set<ProductionValuationEvidence>();
    public DbSet<ProductionPoolDependency> ProductionPoolDependencyRows => Set<ProductionPoolDependency>();
    public DbSet<ProductionStandardCostVariance> ProductionStandardCostVariances => Set<ProductionStandardCostVariance>();
    public DbSet<ProductionConversionCostFact> ProductionConversionCostFacts => Set<ProductionConversionCostFact>();

    public DbSet<UserLogin> UserLogins => Set<UserLogin>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRoleMapping> UserRoleMappings => Set<UserRoleMapping>();
    public DbSet<MenuPermission> MenuPermissions => Set<MenuPermission>();
    public DbSet<RoleMenuPermission> RoleMenuPermissions => Set<RoleMenuPermission>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<MsRunningNo> MsRunningNos => Set<MsRunningNo>();
    public DbSet<AdSmNum> AdSmNums => Set<AdSmNum>();
    public DbSet<AdSmNumDate> AdSmNumDates => Set<AdSmNumDate>();
    public DbSet<AdSmParam> AdSmParams => Set<AdSmParam>();
    public DbSet<MsDept> MsDepts => Set<MsDept>();
    public DbSet<MsProject> MsProjects => Set<MsProject>();

    public DbSet<IvClass> IvClasses => Set<IvClass>();
    public DbSet<IvSubClass> IvSubClasses => Set<IvSubClass>();
    public DbSet<IvClassification> IvClassifications => Set<IvClassification>();
    public DbSet<IvType> IvTypes => Set<IvType>();
    public DbSet<IvStatus> IvStatuses => Set<IvStatus>();
    public DbSet<IvWarehouse> IvWarehouses => Set<IvWarehouse>();
    public DbSet<IvLocation> IvLocations => Set<IvLocation>();
    public DbSet<MsUom> MsUoms => Set<MsUom>();
    public DbSet<MsLhdnUom> MsLhdnUoms => Set<MsLhdnUom>();
    public DbSet<IvStockMaster> IvStockMasters => Set<IvStockMaster>();
    public DbSet<IvItemUomConversion> IvItemUomConversions => Set<IvItemUomConversion>();
    public DbSet<IvLot> IvLots => Set<IvLot>();
    public DbSet<IvBalLoc> IvBalLocs => Set<IvBalLoc>();
    public DbSet<IvTrxBatch> IvTrxBatches => Set<IvTrxBatch>();
    public DbSet<IvTrxBatchDetail> IvTrxBatchDetails => Set<IvTrxBatchDetail>();
    public DbSet<IvTrxHistory> IvTrxHistories => Set<IvTrxHistory>();
    public DbSet<IvStockCountHdr> IvStockCountHdrs => Set<IvStockCountHdr>();
    public DbSet<IvStockCountLine> IvStockCountLines => Set<IvStockCountLine>();
    public DbSet<IvPeriodCloseHdr> IvPeriodCloseHdrs => Set<IvPeriodCloseHdr>();
    public DbSet<IvPeriodCloseBal> IvPeriodCloseBals => Set<IvPeriodCloseBal>();
    public DbSet<StockLedgerEpoch> StockLedgerEpochs => Set<StockLedgerEpoch>();
    public DbSet<StockPosting> StockPostings => Set<StockPosting>();
    public DbSet<StockPostingBranchSequence> StockPostingBranchSequences => Set<StockPostingBranchSequence>();
    public DbSet<StockPeriodSnapshotHdr> StockPeriodSnapshotHdrs => Set<StockPeriodSnapshotHdr>();
    public DbSet<StockPeriodSnapshotLine> StockPeriodSnapshotLines => Set<StockPeriodSnapshotLine>();
    public DbSet<StockValuationFact> StockValuationFacts => Set<StockValuationFact>();
    public DbSet<StockCostState> StockCostStates => Set<StockCostState>();
    public DbSet<StockCostPolicyRevision> StockCostPolicyRevisions => Set<StockCostPolicyRevision>();
    public DbSet<ItemStandardCostRevision> ItemStandardCostRevisions => Set<ItemStandardCostRevision>();
    public DbSet<StockFifoLayer> StockFifoLayers => Set<StockFifoLayer>();
    public DbSet<StockFifoLayerConsumption> StockFifoLayerConsumptions => Set<StockFifoLayerConsumption>();
    public DbSet<PurchaseReceiptCostSettlement> PurchaseReceiptCostSettlements => Set<PurchaseReceiptCostSettlement>();
    public DbSet<PurchaseCostAdjustment> PurchaseCostAdjustments => Set<PurchaseCostAdjustment>();
    public DbSet<StockValuationPeriodSnapshotHdr> StockValuationPeriodSnapshotHdrs => Set<StockValuationPeriodSnapshotHdr>();
    public DbSet<StockValuationPeriodSnapshotLine> StockValuationPeriodSnapshotLines => Set<StockValuationPeriodSnapshotLine>();
    public DbSet<ErpWeb.Model.Entities.Costing.CostingRepairCase> CostingRepairCases => Set<ErpWeb.Model.Entities.Costing.CostingRepairCase>();
    public DbSet<ErpWeb.Model.Entities.Costing.CostingRepairAuditEvent> CostingRepairAuditEvents => Set<ErpWeb.Model.Entities.Costing.CostingRepairAuditEvent>();

    public DbSet<SaCust> SaCusts => Set<SaCust>();
    public DbSet<SaCustAdd> SaCustAdds => Set<SaCustAdd>();
    public DbSet<SaCustContact> SaCustContacts => Set<SaCustContact>();
    public DbSet<SaCustType> SaCustTypes => Set<SaCustType>();
    public DbSet<SaCustGroup> SaCustGroups => Set<SaCustGroup>();
    public DbSet<IvAreaCode> IvAreaCodes => Set<IvAreaCode>();
    public DbSet<IvMsCode> IvMsCodes => Set<IvMsCode>();
    public DbSet<SaCountry> SaCountries => Set<SaCountry>();
    public DbSet<SaCurrency> SaCurrencies => Set<SaCurrency>();
    public DbSet<SaDisGroup> SaDisGroups => Set<SaDisGroup>();
    public DbSet<SaDisCust> SaDisCusts => Set<SaDisCust>();
    public DbSet<SaCurrRate> SaCurrRates => Set<SaCurrRate>();
    public DbSet<SaInvoice> SaInvoices => Set<SaInvoice>();
    public DbSet<SaInvoiceDetail> SaInvoiceDetails => Set<SaInvoiceDetail>();
    public DbSet<SaCdn> SaCdns => Set<SaCdn>();
    public DbSet<SaCdnDetail> SaCdnDetails => Set<SaCdnDetail>();
    public DbSet<SalesReturnCostAllocation> SalesReturnCostAllocations => Set<SalesReturnCostAllocation>();
    public DbSet<SalesReturnStandardCostVariance> SalesReturnStandardCostVariances => Set<SalesReturnStandardCostVariance>();
    public DbSet<SaDo> SaDos => Set<SaDo>();
    public DbSet<SaDoDetail> SaDoDetails => Set<SaDoDetail>();
    public DbSet<SaSo> SaSos => Set<SaSo>();
    public DbSet<SaSoDetail> SaSoDetails => Set<SaSoDetail>();
    public DbSet<SaDeliveryRequest> SaDeliveryRequests => Set<SaDeliveryRequest>();
    public DbSet<SaDeliveryRequestSource> SaDeliveryRequestSources => Set<SaDeliveryRequestSource>();
    public DbSet<SaDeliveryRequestAuditEvent> SaDeliveryRequestAuditEvents => Set<SaDeliveryRequestAuditEvent>();
    public DbSet<SaDeliveryRequestStockReservation> SaDeliveryRequestStockReservations => Set<SaDeliveryRequestStockReservation>();
    public DbSet<SaQt> SaQts => Set<SaQt>();
    public DbSet<SaQtDetail> SaQtDetails => Set<SaQtDetail>();
    public DbSet<SaEInvoiceLog> SaEInvoiceLogs => Set<SaEInvoiceLog>();

    /// <summary>Legacy table dbo.EInvDocSubmission — one row per submitted e-Invoice document.</summary>
    public DbSet<EInvDocSubmission> EInvDocSubmissions => Set<EInvDocSubmission>();
    public DbSet<SaDocApplication> SaDocApplications => Set<SaDocApplication>();
    public DbSet<SaDocApplicationBackfillSkip> SaDocApplicationBackfillSkips => Set<SaDocApplicationBackfillSkip>();
    public DbSet<SaTaxGroup> SaTaxGroups => Set<SaTaxGroup>();
    public DbSet<SaPaymentTerm> SaPaymentTerms => Set<SaPaymentTerm>();
    public DbSet<SaSalesRep> SaSalesReps => Set<SaSalesRep>();
    public DbSet<SaSalesRepTarget> SaSalesRepTargets => Set<SaSalesRepTarget>();
    public DbSet<SaCustSubGroup> SaCustSubGroups => Set<SaCustSubGroup>();
    public DbSet<SaShipVia> SaShipVias => Set<SaShipVia>();
    public DbSet<SaSOType> SaSOTypes => Set<SaSOType>();
    public DbSet<SaComment> SaComments => Set<SaComment>();
    public DbSet<SaShippingLeadTime> SaShippingLeadTimes => Set<SaShippingLeadTime>();
    public DbSet<SaLMW> SaLmws => Set<SaLMW>();

    public DbSet<IvCustPriceGroup> IvCustPriceGroups => Set<IvCustPriceGroup>();
    public DbSet<IvCustPrice> IvCustPrices => Set<IvCustPrice>();
    public DbSet<SaItemCust> SaItemCusts => Set<SaItemCust>();
    public DbSet<SaDisGroupItem> SaDisGroupItems => Set<SaDisGroupItem>();
    public DbSet<SaPriceChangeBatch> SaPriceChangeBatches => Set<SaPriceChangeBatch>();
    public DbSet<SaPriceChangeLine> SaPriceChangeLines => Set<SaPriceChangeLine>();

    public DbSet<PoOrder> PoOrders => Set<PoOrder>();
    public DbSet<PoOrderDetail> PoOrderDetails => Set<PoOrderDetail>();
    public DbSet<PoPr> PoPrs => Set<PoPr>();
    public DbSet<PoPrDetail> PoPrDetails => Set<PoPrDetail>();
    public DbSet<PoInvoice> PoInvoices => Set<PoInvoice>();
    public DbSet<PoInvoiceDetail> PoInvoiceDetails => Set<PoInvoiceDetail>();
    public DbSet<PoCdn> PoCdns => Set<PoCdn>();
    public DbSet<PoCdnDetail> PoCdnDetails => Set<PoCdnDetail>();
    public DbSet<PoSbInvoice> PoSbInvoices => Set<PoSbInvoice>();
    public DbSet<PoSbInvoiceDetail> PoSbInvoiceDetails => Set<PoSbInvoiceDetail>();
    public DbSet<PoSbCdn> PoSbCdns => Set<PoSbCdn>();
    public DbSet<PoSbCdnDetail> PoSbCdnDetails => Set<PoSbCdnDetail>();
    public DbSet<PoCj> PoCjs => Set<PoCj>();
    public DbSet<PoCjDetail> PoCjDetails => Set<PoCjDetail>();
    public DbSet<PoVendor> PoVendors => Set<PoVendor>();
    public DbSet<PoSupplier> PoSuppliers => Set<PoSupplier>();
    public DbSet<PoSupplierAdd> PoSupplierAdds => Set<PoSupplierAdd>();
    public DbSet<PoVendorByItem> PoVendorByItems => Set<PoVendorByItem>();
    public DbSet<PoPurItem> PoPurItems => Set<PoPurItem>();
    public DbSet<PoBuyer> PoBuyers => Set<PoBuyer>();
    public DbSet<PoBuyingTerm> PoBuyingTerms => Set<PoBuyingTerm>();
    public DbSet<PoCategory> PoCategories => Set<PoCategory>();
    public DbSet<PoAuthorised> PoAuthoriseds => Set<PoAuthorised>();
    public DbSet<PoAttachFile> PoAttachFiles => Set<PoAttachFile>();
    public DbSet<PoPrAttachFile> PoPrAttachFiles => Set<PoPrAttachFile>();
    public DbSet<PoDesc> PoDescs => Set<PoDesc>();

    public DbSet<PrBomHdr> PrBomHdrs => Set<PrBomHdr>();
    public DbSet<PrDefBOM> PrDefBOMs => Set<PrDefBOM>();
    public DbSet<PrBomOperation> PrBomOperations => Set<PrBomOperation>();
    public DbSet<PrBomRouteStep> PrBomRouteSteps => Set<PrBomRouteStep>();
    public DbSet<PrBomMachineOption> PrBomMachineOptions => Set<PrBomMachineOption>();
    public DbSet<PrBomLabourStandard> PrBomLabourStandards => Set<PrBomLabourStandard>();
    public DbSet<PrBomLabourRequirement> PrBomLabourRequirements => Set<PrBomLabourRequirement>();
    public DbSet<PrBomMaterialBranchDefault> PrBomMaterialBranchDefaults => Set<PrBomMaterialBranchDefault>();
    public DbSet<PrDefMa> PrDefMas => Set<PrDefMa>();
    public DbSet<PrDefMachine> PrDefMachines => Set<PrDefMachine>();
    public DbSet<PrDefProcess> PrDefProcesses => Set<PrDefProcess>();
    public DbSet<PrDefWcenter> PrDefWcenters => Set<PrDefWcenter>();
    public DbSet<PrCalendar> PrCalendars => Set<PrCalendar>();
    public DbSet<PrHoliday> PrHolidays => Set<PrHoliday>();
    public DbSet<PrMachine> PrMachines => Set<PrMachine>();
    public DbSet<PrMacMaintenance> PrMacMaintenances => Set<PrMacMaintenance>();
    public DbSet<PrMacMaintenanceImage> PrMacMaintenanceImages => Set<PrMacMaintenanceImage>();
    public DbSet<PrMaintenanceReason> PrMaintenanceReasons => Set<PrMaintenanceReason>();
    public DbSet<PrMacSeq> PrMacSeqs => Set<PrMacSeq>();
    public DbSet<PrOperator> PrOperators => Set<PrOperator>();
    public DbSet<PrPreventive> PrPreventives => Set<PrPreventive>();
    public DbSet<PrProcess> PrProcesses => Set<PrProcess>();
    public DbSet<PrShift> PrShifts => Set<PrShift>();
    public DbSet<PrShiftBreak> PrShiftBreaks => Set<PrShiftBreak>();
    public DbSet<PrShiftCalendar> PrShiftCalendars => Set<PrShiftCalendar>();
    public DbSet<PrShiftGroup> PrShiftGroups => Set<PrShiftGroup>();
    public DbSet<PrShiftIcon> PrShiftIcons => Set<PrShiftIcon>();
    public DbSet<PrWorkCentre> PrWorkCentres => Set<PrWorkCentre>();
    public DbSet<PrWorkPefix> PrWorkPefixes => Set<PrWorkPefix>();
    public DbSet<PrSchMa> PrSchMas => Set<PrSchMa>();
    public DbSet<PrSchBom> PrSchBoms => Set<PrSchBom>();
    public DbSet<PrSchLabour> PrSchLabours => Set<PrSchLabour>();
    public DbSet<PrSchMachine> PrSchMachines => Set<PrSchMachine>();
    public DbSet<PrSchProcess> PrSchProcesses => Set<PrSchProcess>();
    public DbSet<PrSchWcenter> PrSchWcenters => Set<PrSchWcenter>();

    public DbSet<ProductionWorkOrder> ProductionWorkOrders => Set<ProductionWorkOrder>();
    public DbSet<PrWorkOrderDemandAllocation> PrWorkOrderDemandAllocations => Set<PrWorkOrderDemandAllocation>();
    public DbSet<ProductionWorkOrderRouteStep> ProductionWorkOrderRouteSteps => Set<ProductionWorkOrderRouteStep>();
    public DbSet<ProductionWorkOrderMaterial> ProductionWorkOrderMaterials => Set<ProductionWorkOrderMaterial>();
    public DbSet<ProductionWorkOrderOperation> ProductionWorkOrderOperations => Set<ProductionWorkOrderOperation>();
    public DbSet<ProductionWorkOrderMachine> ProductionWorkOrderMachines => Set<ProductionWorkOrderMachine>();
    public DbSet<ProductionWorkOrderLabour> ProductionWorkOrderLabours => Set<ProductionWorkOrderLabour>();
    public DbSet<ProductionWorkOrderResource> ProductionWorkOrderResources => Set<ProductionWorkOrderResource>();
    public DbSet<ProductionAuditEvent> ProductionAuditEvents => Set<ProductionAuditEvent>();
    public DbSet<ProductionChangeOrder> ProductionChangeOrders => Set<ProductionChangeOrder>();
    public DbSet<ProductionChangeOrderLine> ProductionChangeOrderLines => Set<ProductionChangeOrderLine>();
    public DbSet<ProductionPostingLink> ProductionPostingLinks => Set<ProductionPostingLink>();
    public DbSet<ProductionMaterialMovement> ProductionMaterialMovements => Set<ProductionMaterialMovement>();
    public DbSet<ProductionMaterialIssueLine> ProductionMaterialIssueLines => Set<ProductionMaterialIssueLine>();
    public DbSet<ProductionBalLot> ProductionBalLots => Set<ProductionBalLot>();
    public DbSet<ProductionBalLotMovement> ProductionBalLotMovements => Set<ProductionBalLotMovement>();
    public DbSet<ProductionMovementAllocation> ProductionMovementAllocations => Set<ProductionMovementAllocation>();
    public DbSet<ProductionLocation> ProductionLocations => Set<ProductionLocation>();
    public DbSet<ProductionOutput> ProductionOutputs => Set<ProductionOutput>();
    public DbSet<ProductionOutputMaterial> ProductionOutputMaterials => Set<ProductionOutputMaterial>();
    public DbSet<WipItemBalLoc> WipItemBalLocs => Set<WipItemBalLoc>();
    public DbSet<PrSchDailyProd> PrSchDailyProds => Set<PrSchDailyProd>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // SQL Server rowversion is DB-generated. SQLite (tests) has no equivalent — send CLR value.
        // Also strip SQL Server-specific filtered index expressions that are invalid in SQLite.
        var provider = Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(byte[]) &&
                        string.Equals(property.Name, "RowVersion", StringComparison.Ordinal))
                    {
                        property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                    }
                }

                // Remove filtered indexes — their filter expressions use SQL Server syntax ([Col] = N'...')
                // which is invalid in SQLite. Uniqueness is relaxed in tests; referential integrity is enforced by logic.
                foreach (var index in entityType.GetIndexes().ToList())
                {
                    // "Relational:Filter" is the annotation used by HasFilter(...)
                    if (index.FindAnnotation("Relational:Filter")?.Value is not null)
                    {
                        entityType.RemoveIndex(index.Properties);
                    }
                }

                // SQL Server Unicode literals in legacy check expressions are not portable to
                // SQLite's test DDL. SQL Server retains these database constraints; services
                // validate the same invariants in SQLite-backed tests.
                foreach (var check in entityType.GetCheckConstraints().ToList())
                {
                    if (check.Sql?.Contains("N'", StringComparison.Ordinal) == true
                        || check.Sql?.Contains("LEN(", StringComparison.OrdinalIgnoreCase) == true
                        || check.Name == "CK_PrMaterialMovement_Type"
                        // Decimal columns are TEXT in SQLite, so RemainingQty <= OriginalQty
                        // compares lexicographically ("7.0" > "10.0") and rejects valid FIFO issues.
                        || check.Name == "CK_StockFifoLayer_Quantities")
                        entityType.RemoveCheckConstraint(check.Name);
                }
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
