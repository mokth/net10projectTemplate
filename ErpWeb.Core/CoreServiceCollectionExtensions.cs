using ErpWeb.Core.Menus;

using ErpWeb.Core.Security;

using ErpWeb.Core.Services;

using ErpWeb.Core.Inventory;

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



        services.AddSingleton<IPasswordPolicy, PasswordPolicy>();

        services.AddSingleton<IMenuDefinitionService, MenuDefinitionService>();

        services.AddSingleton<MenuCache>();



        services.AddScoped<IAuthService, AuthService>();

        services.AddScoped<ICurrentUserService, CurrentUserService>();

        services.AddScoped<ICompanyContext, CompanyContext>();

        services.AddScoped<IUserAdminService, UserAdminService>();

        services.AddScoped<IRoleAdminService, RoleAdminService>();

        services.AddScoped<IPermissionAdminService, PermissionAdminService>();

        services.AddScoped<IRoleMenuPermissionAdminService, RoleMenuPermissionAdminService>();

        services.AddScoped<ICompanyService, CompanyService>();

        services.AddScoped<IBranchService, BranchService>();

        services.AddScoped<IUomService, UomService>();
        services.AddScoped<IItemService, ItemService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<IWarehouseLocationService, WarehouseLocationService>();
        services.AddScoped<IReasonCodeService, ReasonCodeService>();
        services.AddScoped<IPostingEngine, PostingEngine>();
        services.AddScoped<IStockTakeService, StockTakeService>();
        services.AddScoped<ILotReconciliationService, LotReconciliationService>();
        services.AddScoped<IInventoryReconciliationService, InventoryReconciliationService>();
        services.AddScoped<IStockInquiryService, StockInquiryService>();
        services.AddScoped<IInventoryPeriodService, InventoryPeriodService>();
        services.AddScoped<IInventoryAsOfService, InventoryAsOfService>();

        services.AddScoped<IUserRoleSyncService, UserRoleSyncService>();

        services.AddScoped<IMenuService, MenuService>();

        services.AddScoped<IMenuSyncService, MenuSyncService>();

        services.AddScoped<IAccessRightService, AccessRightService>();

        services.AddScoped<INavigationService, NavigationService>();

        return services;

    }

}

