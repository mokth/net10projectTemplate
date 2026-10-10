using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ErpWeb.Core.Inventory;

public sealed class IvStockMasterImageService : IIvStockMasterImageService
{
    private const string WebpContentType = "image/webp";
    private const string ManagedExtension = ".webp";
    private const int CopyBufferSize = 80 * 1024;

    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ItemImageStorageOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<IvStockMasterImageService> _logger;

    public IvStockMasterImageService(
        IIvStockMasterRepository stockMasters,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IOptions<ItemImageStorageOptions> options,
        IHostEnvironment environment,
        ILogger<IvStockMasterImageService> logger,
        IDbContextFactory<AppDbContext>? dbFactory = null)
    {
        _stockMasters = stockMasters;
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<IvMasterOperationResult<IvPreparedStockImage>> PrepareAsync(
        string originalFileName,
        string? contentType,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = originalFileName;
        _ = contentType;

        if (contentLength <= 0)
        {
            return Fail<IvPreparedStockImage>("The selected file is empty.");
        }

        if (contentLength > _options.MaxUploadBytes)
        {
            return Fail<IvPreparedStockImage>(UploadLimitMessage());
        }

        byte[] source;
        try
        {
            source = await ReadBoundedAsync(content, _options.MaxUploadBytes, cancellationToken);
        }
        catch (ImageUploadLimitExceededException)
        {
            return Fail<IvPreparedStockImage>(UploadLimitMessage());
        }

        if (source.Length == 0)
        {
            return Fail<IvPreparedStockImage>("The selected file is empty.");
        }

        try
        {
            using var identifyStream = new MemoryStream(source, writable: false);
            var info = await Image.IdentifyAsync(identifyStream, cancellationToken);
            if (info is null || !IsAllowedFormat(info.Metadata.DecodedImageFormat))
            {
                return Fail<IvPreparedStockImage>("Only JPG, PNG, or WebP images are allowed.");
            }

            if (info.Width > _options.MaxInputSide || info.Height > _options.MaxInputSide)
            {
                return Fail<IvPreparedStockImage>("Image dimensions are too large.");
            }

            if ((long)info.Width * info.Height > _options.MaxInputPixels)
            {
                return Fail<IvPreparedStockImage>("Image dimensions are too large.");
            }

            if (info.FrameCount > 1 || info.FrameMetadataCollection.Count > 1)
            {
                return Fail<IvPreparedStockImage>("Animated or multi-frame images are not supported.");
            }

            using var image = await Image.LoadAsync(
                new MemoryStream(source, writable: false),
                cancellationToken);
            if (image.Frames.Count != 1)
            {
                return Fail<IvPreparedStockImage>("Animated or multi-frame images are not supported.");
            }

            // Orientation must be applied before the metadata is stripped.
            image.Mutate(processing => processing.AutoOrient());
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;

            if (image.Width > _options.MaxDimension || image.Height > _options.MaxDimension)
            {
                image.Mutate(processing => processing.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(_options.MaxDimension, _options.MaxDimension)
                }));
            }

            using var output = new MemoryStream();
            await image.SaveAsync(
                output,
                new WebpEncoder { Quality = _options.WebpQuality },
                cancellationToken);

            if (output.Length <= 0 || output.Length > _options.MaxProcessedBytes)
            {
                return Fail<IvPreparedStockImage>("The processed image is too large.");
            }

            return IvMasterOperationResult<IvPreparedStockImage>.Ok(new IvPreparedStockImage
            {
                Content = output.ToArray(),
                Width = image.Width,
                Height = image.Height,
                ContentType = WebpContentType
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnknownImageFormatException)
        {
            return Fail<IvPreparedStockImage>("The selected file is not a valid image.");
        }
        catch (ImageFormatException)
        {
            return Fail<IvPreparedStockImage>("The selected file is not a valid image.");
        }
        catch (NotSupportedException)
        {
            return Fail<IvPreparedStockImage>("Only JPG, PNG, or WebP images are allowed.");
        }
    }

    public async Task<IvMasterOperationResult<IvStoredStockImage>> StorePreparedAsync(
        string companyCode,
        string itemCode,
        IvPreparedStockImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var company = (companyCode ?? string.Empty).Trim();
        var code = (itemCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(code))
        {
            return Fail<IvStoredStockImage>("Item image scope is invalid.");
        }

        if (image.Content is null || image.Content.Length == 0 || image.Content.Length > _options.MaxProcessedBytes)
        {
            return Fail<IvStoredStockImage>("The processed image is too large.");
        }

        if (image.Width <= 0 || image.Height <= 0
            || image.Width > _options.MaxDimension
            || image.Height > _options.MaxDimension)
        {
            return Fail<IvStoredStockImage>("The processed image dimensions are invalid.");
        }

        var companySegment = HashSegment(company);
        var itemSegment = HashSegment(code);
        var fileName = $"{Guid.NewGuid():N}{ManagedExtension}";
        var relativePath = $"{companySegment}/{itemSegment}/{fileName}";
        if (relativePath.Length > 500)
        {
            return Fail<IvStoredStockImage>("The managed image path is too long.");
        }

        var root = ResolveRoot();
        if (!TryResolveManagedPath(relativePath, company, code, out var absolutePath))
        {
            return Fail<IvStoredStockImage>("Invalid storage path.");
        }

        var directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Fail<IvStoredStockImage>("Invalid storage path.");
        }

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            if (!IsUnderRoot(root, tempPath))
            {
                return Fail<IvStoredStockImage>("Invalid storage path.");
            }

            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true))
            {
                await file.WriteAsync(image.Content.AsMemory(), cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, absolutePath, overwrite: false);
            tempPath = null;

            return IvMasterOperationResult<IvStoredStockImage>.Ok(new IvStoredStockImage
            {
                RelativePath = relativePath
            });
        }
        catch (OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTempFile(tempPath);
            _logger.LogWarning(
                ex,
                "Item image store failed for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                company,
                code,
                "store");
            return Fail<IvStoredStockImage>("Unable to store the item image.");
        }
    }

    public async Task<IvMasterOperationResult<IvStockMasterImageReadResult>> OpenReadAsync(
        string itemCode,
        long? imageId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return Fail<IvStockMasterImageReadResult>(
                "Invalid company or branch context.",
                IvMasterErrorCode.InvalidScope);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Access,
                cancellationToken))
        {
            return Fail<IvStockMasterImageReadResult>("Not authorized.", IvMasterErrorCode.AccessDenied);
        }

        var code = (itemCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<IvStockMasterImageReadResult>("Item code is required.");
        }

        var entity = await _stockMasters.GetByCodeAsync(scope.CompanyCode, code, cancellationToken);
        if (entity is null)
        {
            return Fail<IvStockMasterImageReadResult>("Item image was not found.", IvMasterErrorCode.NotFound);
        }

        var imagePath = entity.ImagePath;
        if (imageId.HasValue)
        {
            if (imageId.Value <= 0 || _dbFactory is null)
            {
                return Fail<IvStockMasterImageReadResult>("Item image was not found.", IvMasterErrorCode.NotFound);
            }

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            imagePath = await db.IvStockMasterImages
                .AsNoTracking()
                .Where(x => x.Uid == imageId.Value
                    && x.CompanyCode == scope.CompanyCode
                    && x.ICode == code)
                .Select(x => x.ImagePath)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Fail<IvStockMasterImageReadResult>("Item image was not found.", IvMasterErrorCode.NotFound);
        }

        if (!TryResolveManagedPath(imagePath, scope.CompanyCode, entity.ICode, out var absolutePath)
            || !File.Exists(absolutePath))
        {
            _logger.LogWarning(
                "Item image path was not available for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                scope.CompanyCode,
                entity.ICode,
                "read");
            return Fail<IvStockMasterImageReadResult>("Item image was not found.", IvMasterErrorCode.NotFound);
        }

        try
        {
            var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                useAsync: true);
            return IvMasterOperationResult<IvStockMasterImageReadResult>.Ok(new IvStockMasterImageReadResult
            {
                Stream = stream,
                ContentType = WebpContentType
            });
        }
        catch (FileNotFoundException)
        {
            return Fail<IvStockMasterImageReadResult>("Item image was not found.", IvMasterErrorCode.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return Fail<IvStockMasterImageReadResult>("Item image was not found.", IvMasterErrorCode.NotFound);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Item image read failed for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                scope.CompanyCode,
                entity.ICode,
                "read");
            throw;
        }
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<IvStockMasterImageRow>>> ListAsync(
        string itemCode,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return Fail<IReadOnlyList<IvStockMasterImageRow>>(
                "Invalid company or branch context.",
                IvMasterErrorCode.InvalidScope);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Access,
                cancellationToken))
        {
            return Fail<IReadOnlyList<IvStockMasterImageRow>>("Not authorized.", IvMasterErrorCode.AccessDenied);
        }

        var code = (itemCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<IReadOnlyList<IvStockMasterImageRow>>("Item code is required.");
        }

        var item = await _stockMasters.GetByCodeAsync(scope.CompanyCode, code, cancellationToken);
        if (item is null)
        {
            return Fail<IReadOnlyList<IvStockMasterImageRow>>("Item was not found.", IvMasterErrorCode.NotFound);
        }

        if (_dbFactory is null)
        {
            return Fail<IReadOnlyList<IvStockMasterImageRow>>(
                "Item image storage is unavailable.",
                IvMasterErrorCode.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.IvStockMasterImages
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.ICode == code)
            .OrderBy(x => x.SortOrder)
            .Select(x => new { x.Uid, x.SortOrder, x.ImagePath })
            .ToListAsync(cancellationToken);

        if ((!string.IsNullOrWhiteSpace(item.ImagePath)
                && !rows.Any(x => string.Equals(x.ImagePath, item.ImagePath, StringComparison.Ordinal)))
            || (rows.Count > 0 && string.IsNullOrWhiteSpace(item.ImagePath)))
        {
            _logger.LogWarning(
                "Stock Master image gallery invariant mismatch for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                scope.CompanyCode,
                code,
                "gallery-list");
        }

        IReadOnlyList<IvStockMasterImageRow> result = rows
            .Select(x => new IvStockMasterImageRow
            {
                Uid = x.Uid,
                SortOrder = x.SortOrder,
                IsPrimary = string.Equals(x.ImagePath, item.ImagePath, StringComparison.Ordinal)
            })
            .ToList();
        return IvMasterOperationResult<IReadOnlyList<IvStockMasterImageRow>>.Ok(result);
    }

    public Task TryDeleteManagedFileAsync(
        string? relativePath,
        string companyCode,
        string itemCode,
        string operation,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var company = (companyCode ?? string.Empty).Trim();
        var code = (itemCode ?? string.Empty).Trim();
        if (!TryResolveManagedPath(relativePath, company, code, out var absolutePath))
        {
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                _logger.LogWarning(
                    "Ignored unmanaged item image cleanup for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                    company,
                    code,
                    operation);
            }

            return Task.CompletedTask;
        }

        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Item image cleanup failed for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                company,
                code,
                operation);
        }

        return Task.CompletedTask;
    }

    private string ResolveRoot()
    {
        var configured = (_options.RootPath ?? string.Empty).Trim();
        var path = Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(_environment.ContentRootPath, configured);
        return Path.GetFullPath(path);
    }

    private bool TryResolveManagedPath(
        string? relativePath,
        string companyCode,
        string itemCode,
        out string absolutePath)
    {
        absolutePath = string.Empty;
        var normalized = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0
            || normalized.Length > 500
            || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], HashSegment(companyCode), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[1], HashSegment(itemCode), StringComparison.OrdinalIgnoreCase)
            || !IsHex(parts[0], 64)
            || !IsHex(parts[1], 64)
            || !parts[2].EndsWith(ManagedExtension, StringComparison.OrdinalIgnoreCase)
            || parts[2].Length != 32 + ManagedExtension.Length
            || !Guid.TryParseExact(parts[2][..^ManagedExtension.Length], "N", out _))
        {
            return false;
        }

        var root = ResolveRoot();
        var candidate = Path.GetFullPath(Path.Combine(root, parts[0], parts[1], parts[2]));
        if (!IsUnderRoot(root, candidate))
        {
            return false;
        }

        absolutePath = candidate;
        return true;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candidateFull = Path.GetFullPath(candidate);
        var separator = Path.DirectorySeparatorChar.ToString();
        var alternateSeparator = Path.AltDirectorySeparatorChar.ToString();
        var rootPrefix = rootFull.EndsWith(separator, StringComparison.Ordinal)
            || rootFull.EndsWith(alternateSeparator, StringComparison.Ordinal)
            ? rootFull
            : rootFull + separator;
        return candidateFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashSegment(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

    private static bool IsHex(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedFormat(IImageFormat? format) =>
        format is not null
        && (string.Equals(format.Name, JpegFormat.Instance.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(format.Name, PngFormat.Instance.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(format.Name, WebpFormat.Instance.Name, StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadBoundedAsync(
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(bytes.AsMemory(), cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new ImageUploadLimitExceededException();
            }

            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private string UploadLimitMessage() =>
        $"Image exceeds the {_options.MaxUploadBytes / (1024 * 1024)} MB upload limit.";

    private static IvMasterOperationResult<T> Fail<T>(
        string message,
        IvMasterErrorCode code = IvMasterErrorCode.Validation) =>
        IvMasterOperationResult<T>.Fail(code, message);

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The primary storage error is more useful than a best-effort temp cleanup error.
        }
        catch (UnauthorizedAccessException)
        {
            // The primary storage error is more useful than a best-effort temp cleanup error.
        }
    }

    private sealed class ImageUploadLimitExceededException : Exception;
}
