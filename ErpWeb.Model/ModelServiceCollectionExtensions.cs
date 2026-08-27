using ErpWeb.Model.Data;
using ErpWeb.Model.Repositories;
using ErpWeb.Model.Repositories.Inventory;
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
        return services;
    }
}
