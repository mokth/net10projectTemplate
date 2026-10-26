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
using SkiaSharp;

namespace ErpWeb.Core.Inventory;

public sealed class IvStockMasterImageService : IIvStockMasterImageService
{
    private const string WebpContentType = "image/webp";
    private const string ManagedExtension = ".webp";
    private const int CopyBufferSize = 80 * 1024;
    private const int RgbaBytesPerPixel = 4;
    private static readonly SKSamplingOptions PhotoSampling = new(SKCubicResampler.Mitchell);

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
            cancellationToken.ThrowIfCancellationRequested();
            using var encodedData = SKData.CreateCopy(source);
            using var inspectCodec = SKCodec.Create(encodedData);
            if (inspectCodec is null)
            {
                return Fail<IvPreparedStockImage>("The selected file is not a valid image.");
            }

            if (!IsAllowedEncodedFormat(inspectCodec.EncodedFormat))
            {
                return Fail<IvPreparedStockImage>("Only JPG, PNG, or WebP images are allowed.");
            }

            var encodedWidth = inspectCodec.Info.Width;
            var encodedHeight = inspectCodec.Info.Height;
            if (encodedWidth <= 0
                || encodedHeight <= 0
                || encodedWidth > _options.MaxInputSide
                || encodedHeight > _options.MaxInputSide
                || (long)encodedWidth * encodedHeight > _options.MaxInputPixels)
            {
                return Fail<IvPreparedStockImage>("Image dimensions are too large.");
            }

            if (IsMultiFrame(inspectCodec))
            {
                return Fail<IvPreparedStockImage>("Animated or multi-frame images are not supported.");
            }

            var origin = inspectCodec.EncodedOrigin;
            cancellationToken.ThrowIfCancellationRequested();

            // Frame inspection can consume the first codec. Decode from a second codec over the same bytes.
            using var decodeCodec = SKCodec.Create(encodedData);
            if (decodeCodec is null)
            {
                return Fail<IvPreparedStockImage>("The selected file is not a valid image.");
            }

            var decoded = DecodeSingleFrameStrict(decodeCodec);
            if (decoded is null)
            {
                return Fail<IvPreparedStockImage>("The selected file is not a valid image.");
            }

            var orientationReplacedSource = false;
            var resizeAllocated = false;
            SKBitmap? oriented = null;
            SKBitmap? resized = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                oriented = NormalizeOrientation(decoded, origin, out orientationReplacedSource);
                cancellationToken.ThrowIfCancellationRequested();
                resized = ResizeIfRequired(oriented, out resizeAllocated);
                cancellationToken.ThrowIfCancellationRequested();
                var output = EncodeLossyWebp(resized);
                if (output.Length <= 0 || output.Length > _options.MaxProcessedBytes)
                {
                    return Fail<IvPreparedStockImage>("The processed image is too large.");
                }

                return IvMasterOperationResult<IvPreparedStockImage>.Ok(new IvPreparedStockImage
                {
                    Content = output,
                    Width = resized.Width,
                    Height = resized.Height,
                    ContentType = WebpContentType
                });
            }
            finally
            {
                if (resizeAllocated)
                {
                    resized?.Dispose();
                }

                if (orientationReplacedSource)
                {
                    oriented?.Dispose();
                }

                decoded.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private static bool IsAllowedEncodedFormat(SKEncodedImageFormat format) =>
        format is SKEncodedImageFormat.Jpeg
            or SKEncodedImageFormat.Png
            or SKEncodedImageFormat.Webp;

    private static bool IsMultiFrame(SKCodec codec)
    {
        if (codec.FrameCount > 1)
        {
            return true;
        }

        var frames = codec.FrameInfo;
        return frames is { Length: > 1 };
    }

    private static SKBitmap? DecodeSingleFrameStrict(SKCodec codec)
    {
        var info = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            bitmap.Dispose();
            return null;
        }

        var result = codec.GetPixels(info, pixels);
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    private static SKBitmap NormalizeOrientation(
        SKBitmap source,
        SKEncodedOrigin origin,
        out bool created)
    {
        if (origin is SKEncodedOrigin.TopLeft || origin == default)
        {
            created = false;
            return source;
        }

        created = true;
        return RemapPixels(source, origin);
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

    private static SKBitmap RemapPixels(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = SwapsAxes(origin);
        var destination = new SKBitmap(new SKImageInfo(
            swap ? source.Height : source.Width,
            swap ? source.Width : source.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        var sourcePixels = source.GetPixelSpan();
        var destinationPixels = destination.GetPixelSpan();
        var sourceRowBytes = source.RowBytes;
        var destinationRowBytes = destination.RowBytes;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                MapPixel(origin, x, y, source.Width, source.Height, out var destX, out var destY);
                var sourceIndex = (y * sourceRowBytes) + (x * RgbaBytesPerPixel);
                var destinationIndex = (destY * destinationRowBytes) + (destX * RgbaBytesPerPixel);
                destinationPixels[destinationIndex] = sourcePixels[sourceIndex];
                destinationPixels[destinationIndex + 1] = sourcePixels[sourceIndex + 1];
                destinationPixels[destinationIndex + 2] = sourcePixels[sourceIndex + 2];
                destinationPixels[destinationIndex + 3] = sourcePixels[sourceIndex + 3];
            }
        }

        return destination;
    }

    private static void MapPixel(
        SKEncodedOrigin origin,
        int x,
        int y,
        int width,
        int height,
        out int destX,
        out int destY)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                destX = width - 1 - x;
                destY = y;
                return;
            case SKEncodedOrigin.BottomRight:
                destX = width - 1 - x;
                destY = height - 1 - y;
                return;
            case SKEncodedOrigin.BottomLeft:
                destX = x;
                destY = height - 1 - y;
                return;
            case SKEncodedOrigin.LeftTop:
                destX = y;
                destY = x;
                return;
            case SKEncodedOrigin.RightTop:
                destX = height - 1 - y;
                destY = x;
                return;
            case SKEncodedOrigin.RightBottom:
                destX = height - 1 - y;
                destY = width - 1 - x;
                return;
            case SKEncodedOrigin.LeftBottom:
                destX = y;
                destY = width - 1 - x;
                return;
            default:
                destX = x;
                destY = y;
                return;
        }
    }

    private SKBitmap ResizeIfRequired(SKBitmap source, out bool created)
    {
        var (targetWidth, targetHeight) = CalculateTargetSize(
            source.Width,
            source.Height,
            _options.MaxDimension);
        if (targetWidth == source.Width && targetHeight == source.Height)
        {
            created = false;
            return source;
        }

        var resized = source.Resize(
            new SKImageInfo(
                targetWidth,
                targetHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul),
            PhotoSampling);
        if (resized is null)
        {
            _logger.LogError("Item image resize failed for operation {Operation}", "prepare");
            throw new InvalidOperationException("Item image encoding failed.");
        }

        created = true;
        return resized;
    }

    private static (int Width, int Height) CalculateTargetSize(int width, int height, int maxDimension)
    {
        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height);
        }

        var scale = maxDimension / (double)Math.Max(width, height);
        return (ScaleSide(width, scale, maxDimension), ScaleSide(height, scale, maxDimension));
    }

    private static int ScaleSide(int side, double scale, int maxDimension)
    {
        var scaled = (int)Math.Round(side * scale, MidpointRounding.AwayFromZero);
        if (scaled < 1)
        {
            return 1;
        }

        return scaled > maxDimension ? maxDimension : scaled;
    }

    private byte[] EncodeLossyWebp(SKBitmap bitmap)
    {
        using var pixmap = bitmap.PeekPixels();
        if (pixmap is null)
        {
            _logger.LogError("Item image pixels were unavailable for operation {Operation}", "prepare");
            throw new InvalidOperationException("Item image encoding failed.");
        }

        var options = new SKWebpEncoderOptions(
            SKWebpEncoderCompression.Lossy,
            _options.WebpQuality);
        using var encoded = SKWebpEncoder.Encode(pixmap, options);
        if (encoded is null)
        {
            _logger.LogError("Item image WebP encode failed for operation {Operation}", "prepare");
            throw new InvalidOperationException("Item image encoding failed.");
        }

        return encoded.ToArray();
    }

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
