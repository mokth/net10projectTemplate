using ErpWeb.Model.Data;
using ErpWeb.Model.Repositories;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ErpWeb.Model;

public static class ModelServiceCollectionExtensions
{
    public static IServiceCollection AddErpWebModel(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlServer(connectionString));
        services.AddScoped<IUserLoginRepository, UserLoginRepository>();
        services.AddScoped<IIvStockMasterRepository, IvStockMasterRepository>();
        services.AddScoped<IIvStockCommonRepository, IvStockCommonRepository>();
        services.AddScoped<IIvStockTransactionRepository, IvStockTransactionRepository>();
        services.AddScoped<IIvStockPostingRepository, IvStockPostingRepository>();
        services.AddScoped<ISaCustRepository, SaCustRepository>();
        services.AddScoped<IPoSupplierRepository, PoSupplierRepository>();
        services.AddScoped<IPoPrRepository, PoPrRepository>();
        services.AddScoped<IPoOrderRepository, PoOrderRepository>();
        services.AddScoped<IPoInvoiceRepository, PoInvoiceRepository>();
        services.AddScoped<ISaInvoiceRepository, SaInvoiceRepository>();
        services.AddScoped<ISaDoRepository, SaDoRepository>();
        services.AddScoped<ISaSoRepository, SaSoRepository>();
        services.AddScoped<ISaCdnRepository, SaCdnRepository>();
        return services;
    }
}
