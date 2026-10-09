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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

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

        var output = Image.Identify(new MemoryStream(result.Data.Content, writable: false));
        Assert.NotNull(output);
        Assert.Equal(80, output!.Width);
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
        Assert.False(svgResult.Succeeded);
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

        using var normalized = await Image.LoadAsync(
            new MemoryStream(largeResult.Data.Content, writable: false));
        Assert.Null(normalized.Metadata.ExifProfile);

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

    private static async Task<byte[]> EncodeAsync(int width, int height, string format)
    {
        using var image = new Image<Rgba32>(width, height);
        using var output = new MemoryStream();
        switch (format)
        {
            case "jpeg":
                await image.SaveAsync(output, new JpegEncoder { Quality = 90 });
                break;
            case "png":
                await image.SaveAsync(output, new PngEncoder());
                break;
            case "webp":
                await image.SaveAsync(output, new WebpEncoder { Quality = 85 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> EncodeGifAsync()
    {
        using var image = new Image<Rgba32>(2, 2);
        using var output = new MemoryStream();
        await image.SaveAsync(output, new GifEncoder());
        return output.ToArray();
    }

    private static async Task<byte[]> EncodeJpegWithExifAsync(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "test");
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)1);
        using var output = new MemoryStream();
        await image.SaveAsync(output, new JpegEncoder { Quality = 85 });
        return output.ToArray();
    }

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
