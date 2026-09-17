using ErpWeb.EInvoiceLib.BL.DataContext;
using ErpWeb.EInvoiceLib.BL.Repository;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Repository;
using ErpWeb.EInvoiceLib.Store;
using ErpWeb.EInvoiceLib.SubmitDoc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ErpWeb.Core.EInvoice;

/// <summary>
/// Registers the LHDN MyInvois e-Invoice adapter (<c>ErpWeb.EInvoiceLib</c>) with the ERP container.
/// <para>
/// The whole adapter is registered <b>scoped</b> so that the per-company credentials and on-behalf
/// TIN held by <see cref="IClientSecretStore"/> cannot leak between concurrent requests for
/// different companies.
/// </para>
/// <para>
/// Application-level connection details (URL, client id/secret, certificate, document version)
/// come from the <c>Einvoice:*</c> configuration section. The per-company overrides are applied at
/// request time through <c>IClientSecretStore.setCompanyCredentials</c>.
/// </para>
/// </summary>
public static class EInvoiceServiceCollectionExtensions
{
    public static IServiceCollection AddErpWebEInvoice(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'DefaultConnection' is required for e-Invoice persistence.");
        }

        services.AddHttpClient();

        services.AddDbContextFactory<EInvoiceContext>(options => options.UseSqlServer(connectionString));

        services.AddScoped<IClientSecretStore, ClientSecretStore>();
        services.AddScoped<IEInvoiceRepository, EInvoiceRepository>();
        services.AddScoped<IE_InvoiceRepository, E_InvoiceRepository>();
        services.AddScoped<ISubmitDocumentHelper, SubmitDocumentHelper>();

        // ERP façade. Registered scoped: it applies the per-company credentials to the scoped
        // IClientSecretStore on every operation, so nothing leaks between companies.
        services.Configure<EInvoiceOptions>(configuration.GetSection(EInvoiceOptions.SectionName));
        services.AddScoped<EInvoiceValidator>();
        services.AddScoped<EInvoiceDocumentMapper>();
        services.AddScoped<ISaEInvoiceService, SaEInvoiceService>();

        return services;
    }
}
