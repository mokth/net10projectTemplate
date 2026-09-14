using ErpWeb.Core.Inventory;

using ErpWeb.Core.Sales;

using ErpWeb.Core.Purchase;

using ErpWeb.Core.Menus;

using ErpWeb.Core.Numbering;

using ErpWeb.Core.Security;

using ErpWeb.Core.Services;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Options;



namespace ErpWeb.Core;



public static class CoreServiceCollectionExtensions

{

    public static IServiceCollection AddErpWebCore(this IServiceCollection services, IConfiguration configuration)

    {

        services.AddSingleton<IValidateOptions<PasswordPolicyOptions>, PasswordPolicyOptionsValidator>();



        services.AddOptions<PasswordPolicyOptions>()

            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName))

            .ValidateOnStart();



        services.AddSingleton<IValidateOptions<MenusOptions>, MenusOptionsValidator>();

        services.AddOptions<MenusOptions>()

            .Bind(configuration.GetSection(MenusOptions.SectionName))

            .ValidateOnStart();



        services.AddOptions<AttachmentStorageOptions>()

            .Bind(configuration.GetSection(AttachmentStorageOptions.SectionName));



        services.AddOptions<PoPrOptions>()

            .Bind(configuration.GetSection(PoPrOptions.SectionName));

        services.AddOptions<PoOrderOptions>()

            .Bind(configuration.GetSection(PoOrderOptions.SectionName));



        services.AddSingleton<IPasswordPolicy, PasswordPolicy>();

        services.AddSingleton<IMenuDefinitionService, MenuDefinitionService>();

        services.AddSingleton<MenuCache>();



        services.AddScoped<IAuthService, AuthService>();

        services.AddScoped<ICurrentUserService, CurrentUserService>();

        services.AddScoped<IInventoryTenantContext, InventoryTenantContext>();

        services.AddScoped<IUserAdminService, UserAdminService>();

        services.AddScoped<IRoleAdminService, RoleAdminService>();

        services.AddScoped<IPermissionAdminService, PermissionAdminService>();

        services.AddScoped<IRoleMenuPermissionAdminService, RoleMenuPermissionAdminService>();

        services.AddScoped<ICompanyService, CompanyService>();

        services.AddScoped<IUserRoleSyncService, UserRoleSyncService>();

        services.AddScoped<IMenuService, MenuService>();

        services.AddScoped<IMenuSyncService, MenuSyncService>();

        services.AddScoped<IAccessRightService, AccessRightService>();

        services.AddScoped<INavigationService, NavigationService>();

        services.AddScoped<IRunningNumberService, RunningNumberService>();

        services.AddScoped<IDocumentNumberingService, DocumentNumberingService>();

        services.AddScoped<IAdSmNumAdminService, AdSmNumAdminService>();

        services.AddScoped<ICurrentDateService, CurrentDateService>();

        services.AddScoped<IIvInventoryLookupService, IvInventoryLookupService>();

        services.AddScoped<IIvMiscReceiptService, IvMiscReceiptService>();

        services.AddScoped<IIvGoodsReceiptService, IvGoodsReceiptService>();

        services.AddScoped<IIvMiscIssueService, IvMiscIssueService>();

        services.AddScoped<IIvVendorReturnService, IvVendorReturnService>();

        services.AddScoped<IIvStockTransferService, IvStockTransferService>();

        services.AddScoped<IIvScrapService, IvScrapService>();

        services.AddScoped<IIvStockReturnService, IvStockReturnService>();

        services.AddScoped<IIvStockAdjustmentService, IvStockAdjustmentService>();

        services.AddScoped<IIvInventoryPostingService, IvInventoryPostingService>();

        services.AddScoped<IIvSpShipmentService, IvSpShipmentService>();

        services.AddScoped<IIvInventoryReconciliationService, IvInventoryReconciliationService>();
        services.AddScoped<ISaAllocationReconciliationService, SaAllocationReconciliationService>();
        services.AddScoped<ISaDocFlowQuery, SaDocFlowQuery>();

        services.AddScoped<IIvStockMasterService, IvStockMasterService>();

        services.AddScoped<IIvInventoryRefService, IvInventoryRefService>();

        services.AddScoped<ISaCustService, SaCustService>();

        services.AddScoped<ISaCustLookupService, SaCustLookupService>();

        services.AddScoped<IPoSupplierService, PoSupplierService>();

        services.AddScoped<IPoSupplierLookupService, PoSupplierLookupService>();

        services.AddScoped<IPoSupplierAttachmentService, PoSupplierAttachmentService>();

        services.AddScoped<IPoSupplierAttachmentFileCleanup>(sp =>
            (IPoSupplierAttachmentFileCleanup)sp.GetRequiredService<IPoSupplierAttachmentService>());

        services.AddScoped<IPoMasterRefService, PoMasterRefService>();

        services.AddScoped<IPoPrService, PoPrService>();

        services.AddScoped<IPoPrAttachmentService, PoPrAttachmentService>();

        services.AddScoped<IPoOrderService, PoOrderService>();

        services.AddScoped<IPoOrderAttachmentService, PoOrderAttachmentService>();

        services.AddScoped<IPoInvoiceService, PoInvoiceService>();

        services.AddScoped<IPoCdnService, PoCdnService>();

        services.AddScoped<ISaSalesRefService, SaSalesRefService>();

        services.AddScoped<ISaInvoiceService, SaInvoiceService>();
        services.AddScoped<ISaDoService, SaDoService>();
        services.AddScoped<ISaSoService, SaSoService>();
        services.AddScoped<ISaCdnService, SaCdnService>();
        services.AddScoped<ISaDocApplication, SaDocApplicationService>();

        return services;

    }

}

