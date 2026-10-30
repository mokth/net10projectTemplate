using System.Runtime.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ErpWeb.Hosting;

/// <summary>
/// Resolves and registers the ErpWeb Data Protection key ring (filesystem persistence, optional DPAPI at rest).
/// </summary>
internal static class DataProtectionKeyRing
{
    public const string ApplicationName = "ErpWeb";
    public const string KeysPathConfigurationKey = "DataProtection:KeysPath";
    public const string DefaultDevelopmentFolderName = "DataProtection-Keys";

    /// <summary>
    /// True when production on Windows will call <see cref="IDataProtectionBuilder.ProtectKeysWithDpapi"/>.
    /// </summary>
    public static bool UsesDpapiAtRest(IHostEnvironment environment) =>
        environment.IsProduction() && OperatingSystem.IsWindows();

    public static string ResolveKeysDirectory(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration[KeysPathConfigurationKey];

        if (environment.IsProduction())
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "Production Data Protection requires an approved key-store configuration on Windows IIS.");
            }

            ValidateProductionKeysPath(
                configuredPath,
                environment.ContentRootPath,
                environment.WebRootPath);
            return Path.GetFullPath(configuredPath!);
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var devPath = Path.Combine(environment.ContentRootPath, DefaultDevelopmentFolderName);
            Directory.CreateDirectory(devPath);
            return Path.GetFullPath(devPath);
        }

        var nonProdPath = Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(nonProdPath);
        return nonProdPath;
    }

    public static IServiceCollection AddErpWebDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var keysDirectory = ResolveKeysDirectory(configuration, environment);
        var dataProtection = services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
            .SetApplicationName(ApplicationName);

        if (UsesDpapiAtRest(environment) && OperatingSystem.IsWindows())
        {
            ApplyDpapiAtRest(dataProtection);
        }

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDpapiAtRest(IDataProtectionBuilder builder)
    {
        builder.ProtectKeysWithDpapi();
    }

    public static void LogStartupDiagnostics(
        IServiceProvider services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var keysPath = ResolveKeysDirectory(configuration, environment);
        var keyManager = services.GetRequiredService<IKeyManager>();
        var allKeys = keyManager.GetAllKeys();
        var now = DateTimeOffset.UtcNow;
        var activeKey = allKeys
            .Where(k => !k.IsRevoked && k.ActivationDate <= now && k.ExpirationDate > now)
            .OrderByDescending(k => k.ActivationDate)
            .FirstOrDefault();

        Log.Information(
            "Data Protection configured: Application={ApplicationName}, KeysPath={KeysPath}, KeyCount={KeyCount}, ActiveKeyId={ActiveKeyId}, DpapiAtRest={DpapiAtRest}",
            ApplicationName,
            keysPath,
            allKeys.Count,
            activeKey?.KeyId.ToString() ?? "(none)",
            UsesDpapiAtRest(environment));
    }

    internal static void ValidateProductionKeysPath(
        string? configuredPath,
        string contentRootPath,
        string? webRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath must be an absolute, operator-provisioned production path " +
                "(configure via DataProtection__KeysPath on IIS).");
        }

        if (!Directory.Exists(configuredPath))
        {
            throw new InvalidOperationException(
                "The Data Protection key directory has not been provisioned. Create and ACL the folder before starting Production.");
        }

        var fullKeysPath = Path.GetFullPath(configuredPath);
        var fullContentRoot = Path.GetFullPath(contentRootPath);

        if (IsSameOrSubdirectory(fullKeysPath, fullContentRoot))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath must be outside the application content root / publish directory.");
        }

        if (!string.IsNullOrWhiteSpace(webRootPath))
        {
            var fullWebRoot = Path.GetFullPath(webRootPath);
            if (IsSameOrSubdirectory(fullKeysPath, fullWebRoot))
            {
                throw new InvalidOperationException(
                    "DataProtection:KeysPath must not be inside wwwroot.");
            }
        }

        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (IsSameOrSubdirectory(fullKeysPath, tempRoot))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath must not be under the system temp directory.");
        }

        if (ContainsWwwRootSegment(fullKeysPath))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath must not reside under a wwwroot publish tree.");
        }
    }

    private static bool IsSameOrSubdirectory(string candidatePath, string parentPath)
    {
        var candidate = NormalizeDirectoryPath(candidatePath);
        var parent = NormalizeDirectoryPath(parentPath);

        if (candidate.Equals(parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(Path.DirectorySeparatorChar))
        {
            full += Path.DirectorySeparatorChar;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool ContainsWwwRootSegment(string fullPath)
    {
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(static p => p.Equals("wwwroot", StringComparison.OrdinalIgnoreCase));
    }
}
