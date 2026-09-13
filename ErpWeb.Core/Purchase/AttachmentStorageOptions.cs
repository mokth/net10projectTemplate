namespace ErpWeb.Core.Purchase;

public sealed class AttachmentStorageOptions
{
    public const string SectionName = "Attachments";

    /// <summary>Private root outside wwwroot. Default: App_Data/attachments</summary>
    public string RootPath { get; set; } = Path.Combine("App_Data", "attachments");
}
