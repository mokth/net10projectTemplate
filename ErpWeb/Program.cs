using ErpWeb.Authentication;
using ErpWeb.Components;
using ErpWeb.Core;
using ErpWeb.Core.Menus;
using ErpWeb.Inventory;
using ErpWeb.Purchase;
using ErpWeb.Sales;
using ErpWeb.Model;
using ErpWeb.UI;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Services;
using ErpWeb.UI.Services.Theme;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Machine/deployment overrides that must never be committed. appsettings.local.json is
    // git-ignored and is where the MyInvois credentials live (see the "Einvoice" section);
    // environment variables (Einvoice__EInv_SecretID, ...) override it in deployed environments.
    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName());

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddDevExpressBlazor(options =>
    {
        options.SizeMode = DevExpress.Blazor.SizeMode.Medium;
    });
    builder.Services.AddMvc();

    // Configure Data Protection to persist keys to file system
    // This prevents antiforgery token errors when the application restarts
    var keysFolder = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysFolder))
        .SetApplicationName("ErpWeb");

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

    builder.Services.AddErpWebModel(connectionString);
    builder.Services.AddErpWebCore(builder.Configuration);
    builder.Services.AddScoped<ICookieSignInService, CookieSignInService>();
    builder.Services.AddScoped<CookiesService>();
    builder.Services.AddScoped<ThemeService>();
    builder.Services.AddScoped<PageNavigationGuard>();

    // The one navigation seam for pages (see ErpWeb.UI/Services/AppNavigation.cs).
    builder.Services.AddScoped<AppNavigation>();

    builder.Services.AddScoped<IGridLayoutStorage, LocalStorageGridLayoutStorage>();

    // Cookie path must match the IIS sub-application (/erpweb). If PathBase is missing at SignIn
    // (proxy/IIS misconfig), the default cookie path becomes "/" and auth/menus break under /erpweb.
    var configuredBasePath = builder.Configuration.GetValue<string>("AppBasePath");
    var authCookiePath = string.IsNullOrWhiteSpace(configuredBasePath)
        ? null
        : "/" + configuredBasePath.Trim('/');

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "ErpWeb.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            if (authCookiePath is not null)
            {
                options.Cookie.Path = authCookiePath;
            }

            options.LoginPath = "/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/unauthorized";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    var app = builder.Build();

    // Deployment base path. When the app is hosted below the site root - an IIS sub-application such as
    // https://host/erpweb - the request path arrives carrying that prefix (ANCM passes it through), so
    // it must be stripped here or routing, MapStaticAssets, the Blazor SignalR hub and every cookie
    // redirect miss. This must run FIRST: everything downstream (including routing) reads the path.
    //
    // "AppBasePath" is blank by default, which keeps the shipped at-the-site-root behaviour unchanged.
    // It is also harmless if it is set and the app is served at the root: UsePathBase only strips a
    // prefix that is actually present, otherwise it is a no-op.
    var appBasePath = builder.Configuration.GetValue<string>("AppBasePath");
    if (!string.IsNullOrWhiteSpace(appBasePath))
    {
        app.UsePathBase("/" + appBasePath.Trim('/'));
        Log.Information("Deployment base path applied: {PathBase}", appBasePath);
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("UserName", httpContext.User.Identity?.Name ?? "anonymous");
        };
        options.GetLevel = (httpContext, elapsed, ex) =>
            ex is not null || httpContext.Response.StatusCode >= 500
                ? Serilog.Events.LogEventLevel.Error
                : Serilog.Events.LogEventLevel.Verbose;
    });

    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapAccountEndpoints();
    app.MapMenuAdminEndpoints();
    app.MapIvStockMasterExportEndpoints();
    app.MapIvBalanceLotExportEndpoints();
    app.MapIvTrxInquiryExportEndpoints();
    app.MapIvStockCardExportEndpoints();
    app.MapIvStockAlertsExportEndpoints();
    app.MapIvStockSummaryExportEndpoints();
    app.MapIvStockCountVarianceExportEndpoints();
    app.MapIvStockValueExportEndpoints();
    app.MapSaCustExportEndpoints();
    app.MapPoSupplierExportEndpoints();
    app.MapPoMasterRefExportEndpoints();
    app.MapSaMasterRefExportEndpoints();
    app.MapSaAnalysisExportEndpoints();
    app.MapSaInquiryExportEndpoints();
    app.MapPoInquiryExportEndpoints();
    app.MapSaItemFamilyExportEndpoints();
    app.MapPoSupplierAttachmentEndpoints();
    app.MapPoPrAttachmentEndpoints();
    app.MapPoOrderAttachmentEndpoints();
    app.MapStaticAssets().AllowAnonymous();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .AddAdditionalAssemblies(typeof(UiAssemblyMarker).Assembly);

    var menusOptions = app.Services.GetRequiredService<IOptions<MenusOptions>>().Value;
    if (menusOptions.SyncOnStartup)
    {
        using var scope = app.Services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IMenuSyncService>();
        var syncResult = sync.SyncFromXmlAsync().GetAwaiter().GetResult();
        if (!syncResult.Success)
        {
            throw new InvalidOperationException(
                "Menu XML synchronization failed at startup: " + string.Join("; ", syncResult.Errors));
        }

        Log.Information(
            "Startup menu sync completed. Inserted={Inserted} Updated={Updated} Disabled={Disabled}",
            syncResult.InsertedCount, syncResult.UpdatedCount, syncResult.DisabledCount);
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
