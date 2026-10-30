using ErpWeb.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ErpWeb.Tests.Other;

[Trait(TestCategories.Name, TestCategories.Shared)]
public class DataProtectionKeyRingTests
{
    [Fact]
    public void Application_name_is_ErpWeb()
    {
        Assert.Equal("ErpWeb", DataProtectionKeyRing.ApplicationName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\keys")]
    public void Production_rejects_missing_or_relative_keys_path(string? configuredPath)
    {
        using var root = new TempDirectory();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionKeyRing.ValidateProductionKeysPath(
                configuredPath,
                root.Path,
                webRootPath: null));

        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_rejects_unprovisioned_directory()
    {
        using var root = new TempDirectory();
        var missing = Path.Combine(root.Path, "missing-keys");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionKeyRing.ValidateProductionKeysPath(
                missing,
                root.Path,
                webRootPath: null));

        Assert.Contains("provisioned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_rejects_keys_path_under_content_root()
    {
        using var root = new TempDirectory();
        var underContent = Path.Combine(root.Path, "DataProtection-Keys");
        Directory.CreateDirectory(underContent);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionKeyRing.ValidateProductionKeysPath(
                underContent,
                root.Path,
                webRootPath: null));

        Assert.Contains("content root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_rejects_keys_path_under_wwwroot()
    {
        using var root = new TempDirectory();
        var webRoot = Path.Combine(root.Path, "wwwroot");
        var keys = Path.Combine(webRoot, "keys");
        Directory.CreateDirectory(keys);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionKeyRing.ValidateProductionKeysPath(
                keys,
                contentRootPath: Path.Combine(root.Path, "app"),
                webRootPath: webRoot));

        Assert.Contains("wwwroot", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_rejects_keys_path_under_temp()
    {
        var tempKeys = Path.Combine(Path.GetTempPath(), "ErpWeb-DpKeys-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempKeys);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                DataProtectionKeyRing.ValidateProductionKeysPath(
                    tempKeys,
                    contentRootPath: Path.Combine(Path.GetTempPath(), "content-" + Guid.NewGuid().ToString("N")),
                    webRootPath: null));

            Assert.Contains("temp", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempKeys, recursive: true);
        }
    }

    [Fact]
    public void Development_uses_content_root_default_when_unconfigured()
    {
        using var root = new TempDirectory();
        var env = CreateEnvironment(Environments.Development, root.Path);
        var config = EmptyConfiguration();

        var resolved = DataProtectionKeyRing.ResolveKeysDirectory(config, env);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root.Path, DataProtectionKeyRing.DefaultDevelopmentFolderName)),
            resolved);
        Assert.True(Directory.Exists(resolved));
    }

    [Fact]
    public void Development_does_not_use_dpapi_at_rest()
    {
        var env = CreateEnvironment(Environments.Development, AppContext.BaseDirectory);
        Assert.False(DataProtectionKeyRing.UsesDpapiAtRest(env));
    }

    [Fact]
    public void Production_on_windows_uses_dpapi_at_rest_flag()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var env = CreateEnvironment(Environments.Production, AppContext.BaseDirectory);
        Assert.True(DataProtectionKeyRing.UsesDpapiAtRest(env));
    }

    [Fact]
    public void Production_on_non_windows_throws_from_resolver()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempDirectory();
        var durable = Path.Combine(root.Path, "durable-keys");
        Directory.CreateDirectory(durable);
        var config = ConfigurationWithKeysPath(durable);
        var env = CreateEnvironment(Environments.Production, root.Path);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionKeyRing.ResolveKeysDirectory(config, env));

        Assert.Contains("Windows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Second_provider_on_same_folder_decrypts_prior_payload()
    {
        using var root = new TempDirectory();
        var keysDir = Path.Combine(root.Path, "shared-keys");
        Directory.CreateDirectory(keysDir);

        var config = ConfigurationWithKeysPath(keysDir);
        var env = CreateEnvironment(Environments.Development, root.Path);

        var payload = ProtectSample(config, env, "continuity-check");
        var roundTrip = UnprotectSample(config, env, payload);

        Assert.Equal("continuity-check", roundTrip);
    }

    [Fact]
    public void Tampered_payload_fails_unprotect()
    {
        using var root = new TempDirectory();
        var config = EmptyConfiguration();
        var env = CreateEnvironment(Environments.Development, root.Path);

        var payload = ProtectSample(config, env, "tamper-test");
        var corrupted = payload[..^4] + "XXXX";

        var services = CreateDataProtectionServices(config, env);
        using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-purpose");

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(corrupted));
    }

    private static string ProtectSample(IConfiguration config, IWebHostEnvironment env, string plaintext)
    {
        using var provider = CreateDataProtectionServices(config, env).BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-purpose");
        return protector.Protect(plaintext);
    }

    private static string UnprotectSample(IConfiguration config, IWebHostEnvironment env, string payload)
    {
        using var provider = CreateDataProtectionServices(config, env).BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-purpose");
        return protector.Unprotect(payload);
    }

    private static ServiceCollection CreateDataProtectionServices(IConfiguration config, IWebHostEnvironment env)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IWebHostEnvironment>(env);
        services.AddErpWebDataProtection(config, env);
        return services;
    }

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration ConfigurationWithKeysPath(string keysPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataProtectionKeyRing.KeysPathConfigurationKey] = keysPath
            })
            .Build();

    private static StubHostEnvironment CreateEnvironment(string environmentName, string contentRoot) =>
        new(environmentName, contentRoot);

    private sealed class StubHostEnvironment(string environmentName, string contentRootPath) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ErpWeb.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public string WebRootPath { get; set; } = Path.Combine(contentRootPath, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ErpWeb-DpTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for test temp folders.
            }
        }
    }
}
