using ErpWeb.Authentication;
using ErpWeb.Components;
using ErpWeb.Core;
using ErpWeb.Core.Menus;
using ErpWeb.Model;
using ErpWeb.UI;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Services.Theme;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

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

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

    builder.Services.AddErpWebModel(connectionString);
    builder.Services.AddErpWebCore(builder.Configuration);
    builder.Services.AddScoped<ICookieSignInService, CookieSignInService>();
    builder.Services.AddScoped<CookiesService>();
    builder.Services.AddScoped<ThemeService>();
    builder.Services.AddScoped<IGridLayoutStorage, LocalStorageGridLayoutStorage>();

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "ErpWeb.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
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
