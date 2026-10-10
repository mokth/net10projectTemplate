using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Inventory;

public sealed class ItemImageStorageOptions
{
    public const string SectionName = "ItemImages";

    public string RootPath { get; set; } = Path.Combine("App_Data", "item-images");
    public long MaxUploadBytes { get; set; } = 8L * 1024 * 1024;
    public int MaxInputPixels { get; set; } = 25_000_000;
    public int MaxInputSide { get; set; } = 12_000;
    public int MaxDimension { get; set; } = 1024;
    public int MaxProcessedBytes { get; set; } = 4 * 1024 * 1024;
    public int WebpQuality { get; set; } = 85;
    public int MaxImagesPerItem { get; set; } = 6;
    public long MaxPendingGalleryBytes { get; set; } = 12L * 1024 * 1024;
}

public sealed class ItemImageStorageOptionsValidator : IValidateOptions<ItemImageStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, ItemImageStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            errors.Add("ItemImages:RootPath must not be empty.");
        }

        if (options.MaxUploadBytes <= 0 || options.MaxUploadBytes > 64L * 1024 * 1024)
        {
            errors.Add("ItemImages:MaxUploadBytes must be between 1 byte and 64 MB.");
        }

        if (options.MaxInputPixels <= 0 || options.MaxInputPixels > 100_000_000)
        {
            errors.Add("ItemImages:MaxInputPixels must be between 1 and 100,000,000.");
        }

        if (options.MaxInputSide <= 0 || options.MaxInputSide > 32_000)
        {
            errors.Add("ItemImages:MaxInputSide must be between 1 and 32,000.");
        }

        if (options.MaxDimension <= 0 || options.MaxDimension > 8_192)
        {
            errors.Add("ItemImages:MaxDimension must be between 1 and 8,192.");
        }

        if (options.MaxProcessedBytes <= 0 || options.MaxProcessedBytes > 64 * 1024 * 1024)
        {
            errors.Add("ItemImages:MaxProcessedBytes must be between 1 byte and 64 MB.");
        }

        if (options.WebpQuality is < 1 or > 100)
        {
            errors.Add("ItemImages:WebpQuality must be between 1 and 100.");
        }

        if (options.MaxImagesPerItem is < 1 or > 10)
        {
            errors.Add("ItemImages:MaxImagesPerItem must be between 1 and 10.");
        }

        if (options.MaxPendingGalleryBytes <= 0
            || options.MaxPendingGalleryBytes > 64L * 1024 * 1024)
        {
            errors.Add("ItemImages:MaxPendingGalleryBytes must be between 1 byte and 64 MB.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
