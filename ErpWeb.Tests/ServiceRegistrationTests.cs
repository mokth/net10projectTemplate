using ErpWeb.Core;
using ErpWeb.Core.Sales;
using ErpWeb.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ErpWeb.Tests;

/// <summary>
/// Guards the DI graph that <c>Program.cs</c> builds at startup.
///
/// A service can compile, pass every unit test, and still be unresolvable at runtime when its
/// constructor dependency was never registered — the unit tests construct services with
/// <c>new</c>, so they never walk the container. That is exactly how <c>ISaQtRepository</c> was
/// first shipped: a registered implementation with no registration, so the Sales Quotation pages
/// threw <c>InvalidOperationException: Unable to resolve service for type 'ISaQtRepository'</c>
/// the first time anyone opened them.
///
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> performs the same call-site validation a
/// resolution would, without instantiating anything, so this fails fast and names the offending
/// service instead of waiting for someone to open the page.
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void Every_registered_service_can_be_constructed()
    {
        var services = CreateCompositionRoot();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider);
    }

    [Theory]
    // One case per sales document menu, resolved through the interface exactly as the Blazor pages
    // inject it.
    [InlineData(typeof(ISaQtService))]
    [InlineData(typeof(ISaSoService))]
    [InlineData(typeof(ISaDoService))]
    [InlineData(typeof(ISaInvoiceService))]
    [InlineData(typeof(ISaCdnService))]
    // Sales-analysis Phase 1: the three analysis pages inject ISaSalesAnalysisService, and the
    // SaSalesRepList target popup injects ISaSalesRefService. Both are resolved through the same
    // composition root the app builds, so a missing registration fails here rather than at the page.
    [InlineData(typeof(ISaSalesAnalysisService))]
    [InlineData(typeof(ISaSalesRefService))]
    public void Sales_document_services_are_resolvable(Type serviceType)
    {
        using var provider = CreateCompositionRoot().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(resolved);
    }

    /// <summary>
    /// Mirrors the registration half of <c>Program.cs</c>: the host services a web app gets for
    /// free (<c>IHttpContextAccessor</c>, logging, <c>IHostEnvironment</c>) have to be supplied
    /// explicitly here, then the two ERP composition methods.
    ///
    /// The connection string is never opened and option sections are bound but not read, so nothing
    /// here touches a database or the file system.
    /// </summary>
    private static ServiceCollection CreateCompositionRoot()
    {
        var services = new ServiceCollection();

        // Host-provided services that WebApplicationBuilder would otherwise add.
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(AppContext.BaseDirectory));

        services.AddErpWebModel(
            "Server=(local);Database=ErpWebRegistrationValidation;Trusted_Connection=True;TrustServerCertificate=True");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Mirrors Program.cs: the e-Invoice adapter needs the same DefaultConnection as the ERP model.
                ["ConnectionStrings:DefaultConnection"] =
                    "Server=(local);Database=ErpWebRegistrationValidation;Trusted_Connection=True;TrustServerCertificate=True"
            })
            .Build();

        // WebApplicationBuilder exposes the root configuration as IConfiguration; the e-Invoice
        // adapter resolves it directly, so the test host has to register it too.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddErpWebCore(configuration);

        return services;
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ErpWeb.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
