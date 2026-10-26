using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SkiaSharp;

namespace ErpWeb.Tests.Inventory.Master;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMasters)]
public sealed class IvStockMasterImageServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "erp-item-images-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("item.jpg", "image/jpeg", "jpeg")]
    [InlineData("item.png", "image/png", "png")]
    [InlineData("item.webp", "image/webp", "webp")]
    public async Task Prepare_AcceptsSupportedFormats(string fileName, string contentType, string format)
    {
        var service = CreateService();
        var bytes = await EncodeAsync(80, 40, format);

        var result = await service.PrepareAsync(
            fileName,
            contentType,
            new MemoryStream(bytes, writable: false),
            bytes.LongLength);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("image/webp", result.Data!.ContentType);
        Assert.InRange(result.Data.Content.Length, 1, 4 * 1024 * 1024);
        AssertWebpWithoutMetadata(result.Data.Content);

        using var sourceData = SKData.CreateCopy(bytes);
        using var sourceCodec = SKCodec.Create(sourceData);
        Assert.NotNull(sourceCodec);
        Assert.Equal(0, sourceCodec!.FrameCount);

        using var output = DecodeWebp(result.Data.Content);
        Assert.Equal(80, output.Width);
        Assert.Equal(40, output.Height);
    }

    [Fact]
    public async Task Prepare_UsesActualFormat_NotExtensionOrContentType()
    {
        var service = CreateService();

        var result = await service.PrepareAsync(
            "renamed.jpg",
            "image/jpeg",
            new MemoryStream("not an image"u8.ToArray(), writable: false),
            12);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("valid image", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prepare_RejectsGifSvgAndOversizedInput()
    {
        var service = CreateService();
        var gif = await EncodeGifAsync();

        var gifResult = await service.PrepareAsync(
            "item.gif",
            "image/gif",
            new MemoryStream(gif, writable: false),
            gif.LongLength);
        var svgBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\" />"u8.ToArray();
        var svgResult = await service.PrepareAsync(
            "item.svg",
            "image/svg+xml",
            new MemoryStream(svgBytes, writable: false),
            svgBytes.LongLength);
        var oversizedResult = await service.PrepareAsync(
            "item.jpg",
            "image/jpeg",
            new MemoryStream([1], writable: false),
            8L * 1024 * 1024 + 1);

        Assert.False(gifResult.Succeeded);
        Assert.Contains("JPG", gifResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(svgResult.Succeeded);
        Assert.Contains("valid image", svgResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(oversizedResult.Succeeded);
        Assert.Contains("8 MB", oversizedResult.Message);
    }

    [Fact]
    public async Task Prepare_RejectsPixelAndSideLimitsBeforeDecode()
    {
        var pixelLimited = CreateService(new ItemImageStorageOptions
        {
            RootPath = _root,
            MaxInputPixels = 4,
            MaxInputSide = 12_000
        });
        var pixelBytes = await EncodeAsync(3, 3, "png");
        var pixelResult = await pixelLimited.PrepareAsync(
            "pixels.png",
            "image/png",
            new MemoryStream(pixelBytes, writable: false),
            pixelBytes.LongLength);

        var sideLimited = CreateService(new ItemImageStorageOptions
        {
            RootPath = _root,
            MaxInputPixels = 25_000_000,
            MaxInputSide = 2
        });
        var sideBytes = await EncodeAsync(3, 1, "png");
        var sideResult = await sideLimited.PrepareAsync(
            "side.png",
            "image/png",
            new MemoryStream(sideBytes, writable: false),
            sideBytes.LongLength);

        Assert.False(pixelResult.Succeeded);
        Assert.False(sideResult.Succeeded);
        Assert.Contains("dimensions", pixelResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dimensions", sideResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prepare_ResizesWithoutUpscalingAndStripsExif()
    {
        var service = CreateService();
        var large = await EncodeJpegWithExifAsync(2048, 1024);
        var largeResult = await service.PrepareAsync(
            "large.jpg",
            "image/jpeg",
            new MemoryStream(large, writable: false),
            large.LongLength);

        Assert.True(largeResult.Succeeded, largeResult.Message);
        Assert.Equal(1024, largeResult.Data!.Width);
        Assert.Equal(512, largeResult.Data.Height);
        AssertWebpWithoutMetadata(largeResult.Data.Content);

        var small = await EncodeAsync(80, 40, "png");
        var smallResult = await service.PrepareAsync(
            "small.png",
            "image/png",
            new MemoryStream(small, writable: false),
            small.LongLength);

        Assert.True(smallResult.Succeeded, smallResult.Message);
        Assert.Equal(80, smallResult.Data!.Width);
        Assert.Equal(40, smallResult.Data.Height);
    }

    [Fact]
    public async Task Prepare_RejectsBmpAsUnsupportedRaster()
    {
        var service = CreateService();
        var bmp = OnePixelBmp;

        var result = await service.PrepareAsync(
            "item.jpg",
            "image/jpeg",
            new MemoryStream(bmp, writable: false),
            bmp.LongLength);

        Assert.False(result.Succeeded);
        Assert.Equal("Only JPG, PNG, or WebP images are allowed.", result.Message);
    }

    [Fact]
    public async Task Prepare_RejectsEmptyUpload()
    {
        var service = CreateService();
        var result = await service.PrepareAsync("empty.png", "image/png", new MemoryStream(), 0);

        Assert.False(result.Succeeded);
        Assert.Equal("The selected file is empty.", result.Message);
    }

    [Fact]
    public async Task Prepare_RejectsRealAnimatedWebp()
    {
        var service = CreateService();
        var animated = CreateTwoFrameWebp();
        using (var data = SKData.CreateCopy(animated))
        using (var codec = SKCodec.Create(data))
        {
            Assert.NotNull(codec);
            Assert.True(codec!.FrameCount > 1 || codec.FrameInfo.Length > 1);
        }

        var result = await service.PrepareAsync(
            "anim.webp",
            "image/webp",
            new MemoryStream(animated, writable: false),
            animated.LongLength);

        Assert.False(result.Succeeded);
        Assert.Equal("Animated or multi-frame images are not supported.", result.Message);
    }

    [Fact]
    public async Task Prepare_RecordsApngFrameBehavior()
    {
        var service = CreateService();
        var apng = CreateTwoFrameApng();
        using var data = SKData.CreateCopy(apng);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        // SkiaSharp 4.153.1 reports FrameCount 0 and an empty FrameInfo for this two-frame APNG.
        // Animated WebP is the mandatory multi-frame rejection. If a future build exposes APNG frames, reject it.
        var multiFrame = codec!.FrameCount > 1 || codec.FrameInfo.Length > 1;

        var result = await service.PrepareAsync(
            "anim.png",
            "image/png",
            new MemoryStream(apng, writable: false),
            apng.LongLength);

        if (multiFrame)
        {
            Assert.False(result.Succeeded);
            Assert.Equal("Animated or multi-frame images are not supported.", result.Message);
            return;
        }

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("image/webp", result.Data!.ContentType);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task Prepare_RejectsTruncatedDecode(string format)
    {
        var full = await EncodeAsync(32, 24, format);
        byte[]? truncated = null;
        var removed = 0;
        SKCodecResult? codecResult = null;
        for (var keep = full.Length - 1; keep > 24; keep--)
        {
            var slice = full[..keep];
            using var data = SKData.CreateCopy(slice);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                continue;
            }

            using var bitmap = new SKBitmap(new SKImageInfo(
                codec.Info.Width,
                codec.Info.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul));
            var decode = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (decode == SKCodecResult.Success)
            {
                continue;
            }

            truncated = slice;
            removed = full.Length - keep;
            codecResult = decode;
            break;
        }

        Assert.True(truncated is not null && codecResult is not null, $"No non-success truncation for {format}.");
        var service = CreateService();
        var result = await service.PrepareAsync(
            "trunc." + format,
            "image/" + format,
            new MemoryStream(truncated, writable: false),
            truncated!.LongLength);

        Assert.False(result.Succeeded);
        Assert.Equal("The selected file is not a valid image.", result.Message);
        Assert.NotEqual(SKCodecResult.Success, codecResult);
        Assert.InRange(removed, 1, full.Length - 1);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public async Task Prepare_NormalizesExifOrientation(int orientation, bool swapsAxes)
    {
        const int width = 48;
        const int height = 24;
        var service = CreateService(new ItemImageStorageOptions
        {
            RootPath = _root,
            WebpQuality = 100
        });
        var jpeg = WithJpegMetadata(await EncodeQuadrantJpegAsync(width, height), (ushort)orientation, includeXmp: false);

        var result = await service.PrepareAsync(
            "oriented.jpg",
            "image/jpeg",
            new MemoryStream(jpeg, writable: false),
            jpeg.LongLength);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(swapsAxes ? height : width, result.Data!.Width);
        Assert.Equal(swapsAxes ? width : height, result.Data.Height);
        AssertWebpWithoutMetadata(result.Data.Content);

        using var output = DecodeWebp(result.Data.Content);
        var expected = ExpectedQuadrants(orientation);
        AssertQuadrant(output, Quadrant.TopLeft, expected.TopLeft);
        AssertQuadrant(output, Quadrant.TopRight, expected.TopRight);
        AssertQuadrant(output, Quadrant.BottomLeft, expected.BottomLeft);
        AssertQuadrant(output, Quadrant.BottomRight, expected.BottomRight);
    }

    [Fact]
    public async Task Prepare_PreservesAlphaAndStripsSourceMetadata()
    {
        var service = CreateService(new ItemImageStorageOptions
        {
            RootPath = _root,
            WebpQuality = 100
        });
        var png = await EncodeAlphaPngAsync();
        var result = await service.PrepareAsync(
            "alpha.png",
            "image/png",
            new MemoryStream(png, writable: false),
            png.LongLength);

        Assert.True(result.Succeeded, result.Message);
        AssertWebpWithoutMetadata(result.Data!.Content);
        using var output = DecodeWebp(result.Data.Content);
        AssertPixel(output, 8, 8, 255, 0, 0, opaque: true);
        AssertPixel(output, output.Width - 9, 8, 0, 255, 0, alpha: 128);
        AssertPixel(output, 8, output.Height - 9, 0, 0, 255, alpha: 0);
        AssertPixel(output, output.Width - 9, output.Height - 9, 255, 255, 255, opaque: true);
    }

    [Fact]
    public async Task Prepare_UsesConfiguredQualityAndProcessedByteLimit()
    {
        var noisy = await EncodeNoisePngAsync(64, 64);
        var low = CreateService(new ItemImageStorageOptions { RootPath = _root, WebpQuality = 20 });
        var high = CreateService(new ItemImageStorageOptions { RootPath = _root, WebpQuality = 90 });
        var lowResult = await low.PrepareAsync("n.png", "image/png", new MemoryStream(noisy, writable: false), noisy.LongLength);
        var highResult = await high.PrepareAsync("n.png", "image/png", new MemoryStream(noisy, writable: false), noisy.LongLength);

        Assert.True(lowResult.Succeeded, lowResult.Message);
        Assert.True(highResult.Succeeded, highResult.Message);
        AssertWebpWithoutMetadata(lowResult.Data!.Content);
        AssertWebpWithoutMetadata(highResult.Data!.Content);
        Assert.NotEqual(lowResult.Data.Content.Length, highResult.Data.Content.Length);

        var limited = CreateService(new ItemImageStorageOptions
        {
            RootPath = _root,
            WebpQuality = 100,
            MaxProcessedBytes = 32
        });
        var limitedResult = await limited.PrepareAsync(
            "n.png",
            "image/png",
            new MemoryStream(noisy, writable: false),
            noisy.LongLength);
        Assert.False(limitedResult.Succeeded);
        Assert.Equal("The processed image is too large.", limitedResult.Message);
    }

    [Fact]
    public async Task Prepare_ResizesPortraitAndSquareWithoutUpscaling()
    {
        var service = CreateService(new ItemImageStorageOptions
        {
            RootPath = _root,
            MaxDimension = 20
        });
        var portrait = await EncodeAsync(40, 80, "png");
        var portraitResult = await service.PrepareAsync(
            "p.png",
            "image/png",
            new MemoryStream(portrait, writable: false),
            portrait.LongLength);
        Assert.True(portraitResult.Succeeded, portraitResult.Message);
        Assert.Equal(10, portraitResult.Data!.Width);
        Assert.Equal(20, portraitResult.Data.Height);

        var square = await EncodeAsync(30, 30, "png");
        var squareResult = await service.PrepareAsync(
            "s.png",
            "image/png",
            new MemoryStream(square, writable: false),
            square.LongLength);
        Assert.True(squareResult.Succeeded, squareResult.Message);
        Assert.Equal(20, squareResult.Data!.Width);
        Assert.Equal(20, squareResult.Data.Height);

        var tiny = await EncodeAsync(10, 8, "png");
        var tinyResult = await service.PrepareAsync(
            "t.png",
            "image/png",
            new MemoryStream(tiny, writable: false),
            tiny.LongLength);
        Assert.True(tinyResult.Succeeded, tinyResult.Message);
        Assert.Equal(10, tinyResult.Data!.Width);
        Assert.Equal(8, tinyResult.Data.Height);
    }

    [Fact]
    public async Task Prepare_CancellationBeforeDecodePropagates()
    {
        var service = CreateService();
        var bytes = await EncodeAsync(16, 16, "png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(
            "item.png",
            "image/png",
            new MemoryStream(bytes, writable: false),
            bytes.LongLength,
            cts.Token));
    }

    [Fact]
    public async Task StoreAndCleanup_UsesManagedRelativePathAndIsIdempotent()
    {
        var service = CreateService();
        var content = new byte[] { 1, 2, 3, 4 };
        var prepared = new IvPreparedStockImage
        {
            Content = content,
            Width = 2,
            Height = 2
        };

        var stored = await service.StorePreparedAsync("DEMO", "A/../../raw-code", prepared);

        Assert.True(stored.Succeeded, stored.Message);
        var relativePath = stored.Data!.RelativePath;
        Assert.True(relativePath.Length <= 500);
        Assert.DoesNotContain("A/../../raw-code", relativePath, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', relativePath);

        var absolutePath = Path.Combine(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(absolutePath));
        Assert.Equal(content, await File.ReadAllBytesAsync(absolutePath));

        await service.TryDeleteManagedFileAsync(relativePath, "DEMO", "A/../../raw-code", "test");
        await service.TryDeleteManagedFileAsync(relativePath, "DEMO", "A/../../raw-code", "test-repeat");

        Assert.False(File.Exists(absolutePath));
    }

    [Fact]
    public async Task Cleanup_RejectsTraversalAndAbsolutePaths()
    {
        var service = CreateService();
        var outside = Path.Combine(_root, "outside.webp");
        await File.WriteAllBytesAsync(outside, [9, 8, 7]);

        await service.TryDeleteManagedFileAsync("../outside.webp", "DEMO", "A100", "test");
        await service.TryDeleteManagedFileAsync(outside, "DEMO", "A100", "test");

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task StoreFailure_DoesNotLeaveTemporaryFile()
    {
        var rootFile = Path.Combine(_root, "root-as-file");
        await File.WriteAllTextAsync(rootFile, "not a directory");
        var service = CreateService(new ItemImageStorageOptions
        {
            RootPath = rootFile
        });

        var result = await service.StorePreparedAsync(
            "DEMO",
            "A100",
            new IvPreparedStockImage
            {
                Content = [1, 2, 3],
                Width = 1,
                Height = 1
            });

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OpenRead_EnforcesAccessAndCompanyScope()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(dbOptions);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(
            repository: new IvStockMasterRepository(factory),
            tenant: tenant,
            access: access.Object);

        var stored = await service.StorePreparedAsync(
            "DEMO",
            "A100",
            new IvPreparedStockImage
            {
                Content = [7, 6, 5],
                Width = 1,
                Height = 1
            });
        Assert.True(stored.Succeeded, stored.Message);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                ImagePath = stored.Data!.RelativePath,
                RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
            });
            await db.SaveChangesAsync();
        }

        var readable = await service.OpenReadAsync("A100");
        Assert.True(readable.Succeeded, readable.Message);
        await using (readable.Data!.Stream)
        {
            Assert.Equal(new byte[] { 7, 6, 5 }, await ReadAllAsync(readable.Data.Stream));
        }
        Assert.Equal("image/webp", readable.Data.ContentType);

        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var denied = await service.OpenReadAsync("A100");
        Assert.False(denied.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, denied.ErrorCode);
    }

    private IvStockMasterImageService CreateService(
        ItemImageStorageOptions? options = null,
        IIvStockMasterRepository? repository = null,
        IInventoryTenantContext? tenant = null,
        IAccessRightService? access = null)
    {
        var optionsValue = options ?? new ItemImageStorageOptions { RootPath = _root };
        var accessMock = new Mock<IAccessRightService>();
        accessMock.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new IvStockMasterImageService(
            repository ?? Mock.Of<IIvStockMasterRepository>(),
            tenant ?? InventoryTenantTestHelper.CreateTenantContext(),
            access ?? accessMock.Object,
            Options.Create(optionsValue),
            new TestHostEnvironment(_root),
            NullLogger<IvStockMasterImageService>.Instance);
    }

    private static readonly byte[] OnePixelGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private static readonly byte[] OnePixelBmp =
    [
        0x42, 0x4D, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00,
        0x28, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00
    ];

    private static Task<byte[]> EncodeAsync(int width, int height, string format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(20, 80, 140, 255));
        return Task.FromResult(EncodeBitmap(bitmap, format, 90));
    }

    private static Task<byte[]> EncodeGifAsync() => Task.FromResult(OnePixelGif);

    private static Task<byte[]> EncodeJpegWithExifAsync(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(180, 40, 40, 255));
        var jpeg = EncodeBitmap(bitmap, "jpeg", 85);
        return Task.FromResult(WithJpegMetadata(jpeg, orientation: 1, includeXmp: true));
    }

    private static Task<byte[]> EncodeQuadrantJpegAsync(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        FillQuadrant(bitmap, 0, 0, width / 2, height / 2, new SKColor(255, 0, 0, 255));
        FillQuadrant(bitmap, width / 2, 0, width, height / 2, new SKColor(0, 255, 0, 255));
        FillQuadrant(bitmap, 0, height / 2, width / 2, height, new SKColor(0, 0, 255, 255));
        FillQuadrant(bitmap, width / 2, height / 2, width, height, new SKColor(255, 255, 255, 255));
        return Task.FromResult(EncodeBitmap(bitmap, "jpeg", 95));
    }

    private static Task<byte[]> EncodeAlphaPngAsync()
    {
        const int width = 32;
        const int height = 32;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        FillQuadrant(bitmap, 0, 0, width / 2, height / 2, new SKColor(255, 0, 0, 255));
        FillQuadrant(bitmap, width / 2, 0, width, height / 2, new SKColor(0, 255, 0, 128));
        FillQuadrant(bitmap, 0, height / 2, width / 2, height, new SKColor(0, 0, 255, 0));
        FillQuadrant(bitmap, width / 2, height / 2, width, height, new SKColor(255, 255, 255, 255));
        return Task.FromResult(EncodeBitmap(bitmap, "png", 100));
    }

    private static Task<byte[]> EncodeNoisePngAsync(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var seed = 17;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                seed = unchecked((seed * 1103515245) + 12345);
                var index = (y * rowBytes) + (x * 4);
                pixels[index] = (byte)(seed >> 16);
                pixels[index + 1] = (byte)(seed >> 8);
                pixels[index + 2] = (byte)seed;
                pixels[index + 3] = 255;
            }
        }

        return Task.FromResult(EncodeBitmap(bitmap, "png", 100));
    }

    private static byte[] EncodeBitmap(SKBitmap bitmap, string format, int quality)
    {
        using var pixmap = bitmap.PeekPixels()
            ?? throw new InvalidOperationException("Fixture pixels were unavailable.");
        using var data = format switch
        {
            "jpeg" => pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)),
            "png" => pixmap.Encode(SKPngEncoderOptions.Default),
            "webp" => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        } ?? throw new InvalidOperationException("Fixture encode failed.");
        return data.ToArray();
    }

    private static byte[] CreateTwoFrameWebp()
    {
        using var first = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var second = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        first.Erase(SKColors.Red);
        second.Erase(SKColors.Blue);
        var frames = new[]
        {
            new SKWebpEncoderFrame(first, TimeSpan.FromMilliseconds(100)),
            new SKWebpEncoderFrame(second, TimeSpan.FromMilliseconds(100))
        };
        using var encoded = SKWebpEncoder.EncodeAnimated(
            frames,
            new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80))
            ?? throw new InvalidOperationException("Animated WebP fixture encode failed.");
        return encoded.ToArray();
    }

    private static byte[] CreateTwoFrameApng()
    {
        var first = EncodeSolidPng(new SKColor(255, 0, 0, 255));
        var second = EncodeSolidPng(new SKColor(0, 0, 255, 255));
        return BuildApng(first, second);
    }

    private static byte[] EncodeSolidPng(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(color);
        return EncodeBitmap(bitmap, "png", 100);
    }

    private static byte[] BuildApng(byte[] firstPng, byte[] secondPng)
    {
        var firstIdat = PngChunk(firstPng, "IDAT");
        var secondIdat = PngChunk(secondPng, "IDAT");
        var ihdr = PngChunk(firstPng, "IHDR");
        using var output = new MemoryStream();
        output.Write(firstPng.AsSpan(0, 8));
        WritePngChunk(output, "IHDR", ihdr);
        WritePngChunk(output, "acTL", [0, 0, 0, 2, 0, 0, 0, 0]);
        WritePngChunk(output, "fcTL", FrameControl(sequence: 0));
        WritePngChunk(output, "IDAT", firstIdat);
        WritePngChunk(output, "fcTL", FrameControl(sequence: 1));
        WritePngChunk(output, "fdAT", SequenceAndData(sequence: 2, secondIdat));
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] FrameControl(int sequence)
    {
        var data = new byte[26];
        data[0] = (byte)(sequence >> 24);
        data[1] = (byte)(sequence >> 16);
        data[2] = (byte)(sequence >> 8);
        data[3] = (byte)sequence;
        data[4] = 0;
        data[5] = 0;
        data[6] = 0;
        data[7] = 2;
        data[8] = 0;
        data[9] = 0;
        data[10] = 0;
        data[11] = 2;
        data[20] = 0;
        data[21] = 0;
        data[22] = 0;
        data[23] = 10;
        data[24] = 0;
        data[25] = 1;
        return data;
    }

    private static byte[] SequenceAndData(int sequence, byte[] idat)
    {
        var data = new byte[4 + idat.Length];
        data[0] = (byte)(sequence >> 24);
        data[1] = (byte)(sequence >> 16);
        data[2] = (byte)(sequence >> 8);
        data[3] = (byte)sequence;
        idat.CopyTo(data, 4);
        return data;
    }

    private static byte[] PngChunk(byte[] png, string type)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            if (png[offset + 4] == typeBytes[0]
                && png[offset + 5] == typeBytes[1]
                && png[offset + 6] == typeBytes[2]
                && png[offset + 7] == typeBytes[3])
            {
                var data = new byte[length];
                Buffer.BlockCopy(png, offset + 8, data, 0, length);
                return data;
            }

            offset += 12 + length;
        }

        throw new InvalidOperationException($"PNG chunk {type} was not found.");
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var length = data.Length;
        output.WriteByte((byte)(length >> 24));
        output.WriteByte((byte)(length >> 16));
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
        output.Write(typeBytes);
        output.Write(data);
        var crcSource = new byte[4 + data.Length];
        typeBytes.CopyTo(crcSource, 0);
        data.CopyTo(crcSource, 4);
        var crc = PngCrc(crcSource);
        output.WriteByte((byte)(crc >> 24));
        output.WriteByte((byte)(crc >> 16));
        output.WriteByte((byte)(crc >> 8));
        output.WriteByte((byte)crc);
    }

    private static uint PngCrc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (uint)-(crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] WithJpegMetadata(byte[] jpeg, ushort orientation, bool includeXmp)
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        output.Write(BuildExifApp1(orientation));
        if (includeXmp)
        {
            output.Write(BuildXmpApp1());
        }

        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    private static byte[] BuildExifApp1(ushort orientation)
    {
        var tiff = new byte[]
        {
            (byte)'I', (byte)'I', 0x2A, 0x00,
            0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01,
            0x03, 0x00,
            0x01, 0x00, 0x00, 0x00,
            (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };
        var payload = new byte[6 + tiff.Length];
        payload[0] = (byte)'E';
        payload[1] = (byte)'x';
        payload[2] = (byte)'i';
        payload[3] = (byte)'f';
        tiff.CopyTo(payload, 6);
        return AppSegment(0xE1, payload);
    }

    private static byte[] BuildXmpApp1()
    {
        var header = "http://ns.adobe.com/xap/1.0/\0"u8;
        var packet = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"u8;
        var payload = new byte[header.Length + packet.Length];
        header.CopyTo(payload);
        packet.CopyTo(payload.AsSpan(header.Length));
        return AppSegment(0xE1, payload);
    }

    private static byte[] AppSegment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        var segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = marker;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)length;
        payload.CopyTo(segment, 4);
        return segment;
    }

    private static SKBitmap DecodeWebp(byte[] webp)
    {
        using var data = SKData.CreateCopy(webp);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        var info = new SKImageInfo(
            codec!.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        Assert.Equal(SKCodecResult.Success, result);
        return bitmap;
    }

    private static void AssertWebpWithoutMetadata(byte[] webp)
    {
        Assert.True(webp.Length > 12);
        Assert.Equal((byte)'R', webp[0]);
        Assert.Equal((byte)'I', webp[1]);
        Assert.Equal((byte)'F', webp[2]);
        Assert.Equal((byte)'F', webp[3]);
        Assert.Equal((byte)'W', webp[8]);
        Assert.Equal((byte)'E', webp[9]);
        Assert.Equal((byte)'B', webp[10]);
        Assert.Equal((byte)'P', webp[11]);
        Assert.False(WebpHasChunk(webp, "EXIF"));
        Assert.False(WebpHasChunk(webp, "XMP "));
        Assert.False(WebpHasChunk(webp, "ICCP"));
    }

    private static bool WebpHasChunk(byte[] webp, string fourCc)
    {
        var offset = 12;
        while (offset + 8 <= webp.Length)
        {
            if (webp[offset] == fourCc[0]
                && webp[offset + 1] == fourCc[1]
                && webp[offset + 2] == fourCc[2]
                && webp[offset + 3] == fourCc[3])
            {
                return true;
            }

            var size = webp[offset + 4]
                | (webp[offset + 5] << 8)
                | (webp[offset + 6] << 16)
                | (webp[offset + 7] << 24);
            if (size < 0)
            {
                return false;
            }

            offset += 8 + size + (size & 1);
        }

        return false;
    }

    private static void FillQuadrant(SKBitmap bitmap, int left, int top, int right, int bottom, SKColor color)
    {
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var index = (y * rowBytes) + (x * 4);
                pixels[index] = color.Red;
                pixels[index + 1] = color.Green;
                pixels[index + 2] = color.Blue;
                pixels[index + 3] = color.Alpha;
            }
        }
    }

    private enum QuadrantColor
    {
        Red,
        Green,
        Blue,
        White
    }

    private enum Quadrant
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private readonly record struct QuadrantMap(
        QuadrantColor TopLeft,
        QuadrantColor TopRight,
        QuadrantColor BottomLeft,
        QuadrantColor BottomRight);

    private static QuadrantMap ExpectedQuadrants(int orientation) => orientation switch
    {
        1 => new(QuadrantColor.Red, QuadrantColor.Green, QuadrantColor.Blue, QuadrantColor.White),
        2 => new(QuadrantColor.Green, QuadrantColor.Red, QuadrantColor.White, QuadrantColor.Blue),
        3 => new(QuadrantColor.White, QuadrantColor.Blue, QuadrantColor.Green, QuadrantColor.Red),
        4 => new(QuadrantColor.Blue, QuadrantColor.White, QuadrantColor.Red, QuadrantColor.Green),
        5 => new(QuadrantColor.Red, QuadrantColor.Blue, QuadrantColor.Green, QuadrantColor.White),
        6 => new(QuadrantColor.Blue, QuadrantColor.Red, QuadrantColor.White, QuadrantColor.Green),
        7 => new(QuadrantColor.White, QuadrantColor.Green, QuadrantColor.Blue, QuadrantColor.Red),
        8 => new(QuadrantColor.Green, QuadrantColor.White, QuadrantColor.Red, QuadrantColor.Blue),
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, null)
    };

    private static void AssertQuadrant(SKBitmap bitmap, Quadrant quadrant, QuadrantColor expected)
    {
        var x = quadrant is Quadrant.TopLeft or Quadrant.BottomLeft ? 4 : bitmap.Width - 5;
        var y = quadrant is Quadrant.TopLeft or Quadrant.TopRight ? 4 : bitmap.Height - 5;
        var pixel = ReadPixel(bitmap, x, y);
        switch (expected)
        {
            case QuadrantColor.Red:
                Assert.True(pixel.Red > 180 && pixel.Green < 80 && pixel.Blue < 80, Describe(pixel));
                break;
            case QuadrantColor.Green:
                Assert.True(pixel.Green > 180 && pixel.Red < 80 && pixel.Blue < 80, Describe(pixel));
                break;
            case QuadrantColor.Blue:
                Assert.True(pixel.Blue > 180 && pixel.Red < 80 && pixel.Green < 80, Describe(pixel));
                break;
            case QuadrantColor.White:
                Assert.True(pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200, Describe(pixel));
                break;
        }
    }

    private static void AssertPixel(
        SKBitmap bitmap,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        bool opaque = false,
        int alpha = 255)
    {
        var pixel = ReadPixel(bitmap, x, y);
        if (opaque)
        {
            Assert.True(pixel.Alpha >= 240, Describe(pixel));
        }
        else if (alpha == 0)
        {
            Assert.True(pixel.Alpha <= 20, Describe(pixel));
        }
        else
        {
            Assert.InRange(pixel.Alpha, 40, 220);
            Assert.True(pixel.Green > pixel.Red && pixel.Green > pixel.Blue, Describe(pixel));
        }

        if (opaque && red == 255 && green == 0)
        {
            Assert.True(pixel.Red > 180 && pixel.Green < 80, Describe(pixel));
        }

        if (opaque && red == 255 && green == 255)
        {
            Assert.True(pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200, Describe(pixel));
        }

        _ = blue;
    }

    private readonly record struct Rgba(byte Red, byte Green, byte Blue, byte Alpha);

    private static Rgba ReadPixel(SKBitmap bitmap, int x, int y)
    {
        var pixels = bitmap.GetPixelSpan();
        var index = (y * bitmap.RowBytes) + (x * 4);
        return new Rgba(pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]);
    }

    private static string Describe(Rgba pixel) => $"R={pixel.Red} G={pixel.Green} B={pixel.Blue} A={pixel.Alpha}";

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;

        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ErpWeb.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
