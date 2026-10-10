using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvStockMasterEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? ICode { get; set; }
    [SupplyParameterFromQuery(Name = "copy")] public string? CopyFrom { get; set; }

    [Inject] private IIvStockMasterService StockMasters { get; set; } = default!;
    [Inject] private IIvStockMasterImageService StockMasterImages { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IOptions<ItemImageStorageOptions> ItemImageOptions { get; set; } = default!;

    private int _subClassLoadVersion;
    private int _locationLoadVersion;
    private string? _pendingSubClass;
    private string? _pendingLocation;
    private string _cleanSnapshot = string.Empty;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private readonly List<IvStockMasterImageRow> _persistedImages = [];
    private readonly List<IvPendingStockImage> _pendingImages = [];
    private readonly HashSet<long> _pendingImageRemovals = [];
    private long? _selectedExistingImageId;
    private Guid? _selectedPendingImageToken;
    private long? _requestedPrimaryExistingImageId;
    private Guid? _requestedPrimaryPendingImageToken;
    private string? _imageError;
    private bool _isPreparingImage;
    private int _imageInputKey;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool SubClassesLoading;
    protected bool LocationsLoading;
    protected bool CanAdd;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected IvStockMasterEditVm Model { get; set; } = CreateBlank();
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    protected IReadOnlyList<IvCodeLookupRow> Types { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SubClasses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classifications { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Uoms { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Locations { get; set; } = [];

    protected IReadOnlyList<IvExpiryControlOption> ExpiryControlOptions { get; } =
    [
        new(IvExpiryControlModes.None, "None"),
        new(IvExpiryControlModes.Optional, "Optional"),
        new(IvExpiryControlModes.Required, "Required")
    ];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;

    protected string PageHeading => IsNewMode
        ? (string.IsNullOrWhiteSpace(CopyFrom) ? "New item" : "Copy item")
        : IsEditMode
            ? "Edit item"
            : "View item";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected string SupplyMethodDisplay => (Model.MfgType ?? "BUY").Trim().ToUpperInvariant() switch
    {
        "MAKE" => "MAKE — Manufactured",
        "PHANTOM" => "PHANTOM — Internal / Phantom",
        _ => "BUY — Purchased / Stock"
    };

    protected string ClassificationDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Classification))
            {
                return "—";
            }

            var row = Classifications.FirstOrDefault(x =>
                string.Equals(x.Code, Model.Classification, StringComparison.OrdinalIgnoreCase));
            return row?.DisplayText ?? Model.Classification;
        }
    }

    protected bool IsDirty =>
        !IsViewMode
        && !IsLoading
        && (!string.Equals(_cleanSnapshot, Snapshot(Model), StringComparison.Ordinal)
            || HasPendingImageChange);

    protected bool HasPendingImageChange => _pendingImages.Count > 0
        || _pendingImageRemovals.Count > 0
        || (_requestedPrimaryExistingImageId is long requestedExisting
            && _persistedImages.All(x => x.Uid != requestedExisting || !x.IsPrimary))
        || _requestedPrimaryPendingImageToken is not null;
    protected bool IsPreparingImage => _isPreparingImage;
    protected string? ImageError => _imageError;
    protected long MaxImageUploadBytes => ItemImageOptions.Value.MaxUploadBytes;
    protected int MaxImagesPerItem => ItemImageOptions.Value.MaxImagesPerItem;
    protected long MaxPendingGalleryBytes => ItemImageOptions.Value.MaxPendingGalleryBytes;
    protected int GalleryCount => _persistedImages.Count(x => IsImageAvailable(x.Uid)) + _pendingImages.Count;
    protected int RemainingImageSlots => Math.Max(0, MaxImagesPerItem - GalleryCount);
    protected bool CanModifyGallery => !IsViewMode && !IsSubmitting && (IsNewMode ? CanAdd : CanEdit);
    protected bool CanChooseImage =>
        CanModifyGallery
        && !_isPreparingImage
        && RemainingImageSlots > 0;
    protected bool CanRemoveSelectedImage => CanModifyGallery
        && (_selectedPendingImageToken is not null
            || (_selectedExistingImageId is long id && IsImageAvailable(id)));
    protected bool CanSetSelectedPrimary => CanModifyGallery
        && (_selectedPendingImageToken is not null || _selectedExistingImageId is not null)
        && !IsSelectedImagePrimary;
    protected bool IsSelectedImagePrimary =>
        (_selectedExistingImageId is long existingId && IsPrimaryImage(existingId))
        || (_selectedPendingImageToken is Guid pendingToken && IsPrimaryImage(pendingToken));
    protected string GalleryCountLabel => $"{GalleryCount} / {MaxImagesPerItem} images";
    protected bool HasImagePreview => !string.IsNullOrWhiteSpace(ImagePreviewUrl);
    protected string? ImagePreviewUrl
    {
        get
        {
            if (_selectedPendingImageToken is Guid pendingToken
                && FindPendingImage(pendingToken) is { } pending)
            {
                return ToDataUrl(pending.Image);
            }

            if (_selectedExistingImageId is long selectedId && IsImageAvailable(selectedId))
            {
                return ImageUrl(selectedId);
            }

            if (EffectivePrimaryPending is { } primaryPending)
            {
                return ToDataUrl(primaryPending.Image);
            }

            if (EffectivePrimaryPersisted is { } primaryImage)
            {
                return ImageUrl(primaryImage.Uid);
            }

            return Model.HasImage && !string.IsNullOrWhiteSpace(Model.ICode)
                ? Navigation.Resolve($"/inventory/item-image?iCode={Uri.EscapeDataString(Model.ICode)}")
                : null;
        }
    }

    protected string ImageAlt => string.IsNullOrWhiteSpace(Model.IDesc)
        ? $"Item {Model.ICode}"
        : $"{Model.ICode} — {Model.IDesc}";

    protected string ImageActionLabel => "ADD IMAGES";

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanAdd = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Add);
            CanEdit = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Edit);
            await LoadLookupsAsync();
            _lookupsLoaded = true;
        }

        var key = $"{Mode}|{ICode}|{CopyFrom}";
        if (string.Equals(key, _loadedKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        await LoadPageAsync();
    }

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected async Task OnClassChangedAsync(string? value)
    {
        Model.IClassCode = value;
        Model.ISubClassCode = null;
        _pendingSubClass = null;
        await LoadSubClassesAsync(Model.IClassCode);
    }

    protected async Task OnWarehouseChangedAsync(string? value)
    {
        Model.DefWarehouse = value;
        Model.DefLocation = null;
        _pendingLocation = null;
        await LoadLocationsAsync(Model.DefWarehouse);
    }

    protected void OnLotControlChanged(bool value)
    {
        Model.LotControl = value;
        if (!value)
        {
            Model.ExpiryControl = IvExpiryControlModes.None;
        }
    }

    protected void OnExpiryControlChanged(string? value) =>
        Model.ExpiryControl = value ?? IvExpiryControlModes.None;

    protected async Task OnSaveAsync()
    {
        if (IsSubmitting || IsViewMode)
        {
            return;
        }

        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await StockMasters.SaveAsync(
                Model,
                IsNewMode,
                BuildGalleryChanges());
            if (result.Succeeded)
            {
                ClearPendingImageState();
                Navigation.NavigateTo("/inventory/items");
                return;
            }

            if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ConcurrencyVisible = true;
                ErrorMessage = result.Message ?? "Concurrency conflict.";
            }
            else
            {
                ErrorMessage = result.Message ?? "Unable to save item.";
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (IsDirty)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        ClearPendingImageState();
        Navigation.NavigateTo("/inventory/items");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/items");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (string.IsNullOrWhiteSpace(Model.ICode))
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/items/edit/{Uri.EscapeDataString(Model.ICode)}");
    }

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        ClearPendingImageState();
        Navigation.NavigateTo("/inventory/items");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (string.IsNullOrWhiteSpace(Model.ICode) && string.IsNullOrWhiteSpace(ICode))
        {
            return;
        }

        var code = Model.ICode;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = ICode!;
        }

        var result = await StockMasters.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to reload item.";
            return;
        }

        Model = Clone(result.Data);
        ClearPendingImageState();
        await LoadGalleryAsync(code, clearPending: true);
        ValidationErrors.Clear();
        ErrorMessage = null;
        StatusMessage = "Loaded latest version.";
        await AfterModelLoadedAsync(preservePending: false);
        CaptureCleanSnapshot();
    }

    protected async Task KeepMyChangesAsync()
    {
        ConcurrencyVisible = false;
        var code = Model.ICode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var result = await StockMasters.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to refresh concurrency token.";
            return;
        }

        // Keep field edits; adopt latest RowVersion so the next save can overwrite.
        Model.RowVersion = result.Data.RowVersion;
        Model.HasImage = result.Data.HasImage;
        await LoadGalleryAsync(code, clearPending: false);
        StatusMessage = "Kept your changes. Save again to overwrite.";
        await Task.CompletedTask;
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected async Task OnImageSelectedAsync(InputFileChangeEventArgs args)
    {
        if (!CanChooseImage)
        {
            return;
        }

        _imageError = null;
        _imageInputKey++;
        var remaining = RemainingImageSlots;
        if (args.FileCount > remaining)
        {
            _imageError = $"Maximum {MaxImagesPerItem} images per item. You can add {remaining} more.";
            return;
        }

        var files = args.GetMultipleFiles(args.FileCount);
        var existingPendingBytes = _pendingImages.Sum(x => (long)x.Image.Content.Length);
        var preparedImages = new List<IvPendingStockImage>(files.Count);
        var preparedBytes = 0L;
        _isPreparingImage = true;
        try
        {
            foreach (var file in files)
            {
                if (file.Size <= 0)
                {
                    _imageError = $"{file.Name}: the selected file is empty.";
                    return;
                }

                if (file.Size > MaxImageUploadBytes)
                {
                    _imageError = $"{file.Name}: image exceeds the {MaxImageUploadBytes / (1024 * 1024)} MB upload limit.";
                    return;
                }

                await using var stream = file.OpenReadStream(MaxImageUploadBytes);
                var result = await StockMasterImages.PrepareAsync(
                    file.Name,
                    file.ContentType,
                    stream,
                    file.Size);
                if (!result.Succeeded || result.Data is null)
                {
                    _imageError = $"{file.Name}: {result.Message ?? "The selected image could not be prepared."}";
                    return;
                }

                preparedBytes += result.Data.Content.LongLength;
                if (existingPendingBytes + preparedBytes > MaxPendingGalleryBytes)
                {
                    _imageError = $"Pending images exceed the {MaxPendingGalleryBytes / (1024 * 1024)} MB gallery memory limit.";
                    return;
                }

                preparedImages.Add(new IvPendingStockImage
                {
                    Token = Guid.NewGuid(),
                    Image = result.Data
                });
            }

            _pendingImages.AddRange(preparedImages);
            if (_selectedExistingImageId is null && _selectedPendingImageToken is null)
            {
                EnsureSelectedImage();
            }
        }
        catch (IOException)
        {
            _imageError = "The selected image could not be read.";
        }
        catch (InvalidOperationException)
        {
            _imageError = "The selected image could not be read.";
        }
        finally
        {
            _isPreparingImage = false;
        }
    }

    protected void RemoveSelectedImage()
    {
        if (!CanRemoveSelectedImage)
        {
            return;
        }

        if (_selectedPendingImageToken is Guid pendingToken)
        {
            _pendingImages.RemoveAll(x => x.Token == pendingToken);
            if (_requestedPrimaryPendingImageToken == pendingToken)
            {
                _requestedPrimaryPendingImageToken = null;
            }
        }
        else if (_selectedExistingImageId is long imageId)
        {
            _pendingImageRemovals.Add(imageId);
            if (_requestedPrimaryExistingImageId == imageId)
            {
                _requestedPrimaryExistingImageId = null;
            }
        }

        _selectedExistingImageId = null;
        _selectedPendingImageToken = null;
        _imageError = null;
        EnsureSelectedImage();
    }

    protected void SelectExistingImage(IvStockMasterImageRow image)
    {
        if (IsImageAvailable(image.Uid))
        {
            _selectedExistingImageId = image.Uid;
            _selectedPendingImageToken = null;
        }
    }

    protected void SelectPendingImage(Guid token)
    {
        if (FindPendingImage(token) is not null)
        {
            _selectedPendingImageToken = token;
            _selectedExistingImageId = null;
        }
    }

    protected void SetSelectedPrimary()
    {
        if (!CanSetSelectedPrimary)
        {
            return;
        }

        if (_selectedExistingImageId is long imageId)
        {
            _requestedPrimaryExistingImageId = _persistedImages.Any(x => x.Uid == imageId && x.IsPrimary)
                ? null
                : imageId;
            _requestedPrimaryPendingImageToken = null;
        }
        else if (_selectedPendingImageToken is Guid token)
        {
            _requestedPrimaryPendingImageToken = token;
            _requestedPrimaryExistingImageId = null;
        }
    }

    protected static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "—";

    protected static string FormatDec(decimal? value, string format = "n4") =>
        value.HasValue ? value.Value.ToString(format) : "—";

    protected static string ExpiryControlLabel(string? value) =>
        value switch
        {
            IvExpiryControlModes.Optional => "Optional",
            IvExpiryControlModes.Required => "Required",
            _ => "None"
        };

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConcurrencyVisible = false;
        ConfirmDiscardVisible = false;
        ClearPendingImageState();

        try
        {
            if (IsNewMode)
            {
                if (!string.IsNullOrWhiteSpace(CopyFrom))
                {
                    var copyResult = await StockMasters.GetAsync(CopyFrom);
                    if (!copyResult.Succeeded || copyResult.Data is null)
                    {
                        ErrorMessage = copyResult.Message ?? "Unable to copy item.";
                        Model = CreateBlank();
                    }
                    else
                    {
                        Model = Clone(copyResult.Data);
                        // Product Definitions/BOMs are not copied.  A copied MAKE
                        // item must therefore start as the normal BUY display state.
                        Model.MfgType = "BUY";
                        Model.ICode = string.Empty;
                        Model.Barcode = null;
                        Model.RowVersion = null;
                        Model.CreatedBy = null;
                        Model.CreatedDate = null;
                        Model.ModifiedBy = null;
                        Model.ModifiedDate = null;
                        Model.HasImage = false;
                        StatusMessage = $"Copied from {CopyFrom}.";
                    }
                }
                else
                {
                    Model = CreateBlank();
                }

                await AfterModelLoadedAsync(preservePending: true);
                CaptureCleanSnapshot();
                return;
            }

            var code = ICode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
            {
                ErrorMessage = "Item code is required.";
                Model = CreateBlank();
                return;
            }

            var result = await StockMasters.GetAsync(code);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Item was not found.";
                Model = CreateBlank();
                return;
            }

            Model = Clone(result.Data);
            await LoadGalleryAsync(code, clearPending: true);
            await AfterModelLoadedAsync(preservePending: true);
            CaptureCleanSnapshot();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AfterModelLoadedAsync(bool preservePending)
    {
        if (preservePending)
        {
            _pendingSubClass = Model.ISubClassCode;
            _pendingLocation = Model.DefLocation;
        }

        await LoadSubClassesAsync(Model.IClassCode);
        await LoadLocationsAsync(Model.DefWarehouse);

        if (!string.IsNullOrWhiteSpace(_pendingSubClass) &&
            SubClasses.Any(x => string.Equals(x.Code, _pendingSubClass, StringComparison.OrdinalIgnoreCase)))
        {
            Model.ISubClassCode = _pendingSubClass;
        }

        if (!string.IsNullOrWhiteSpace(_pendingLocation) &&
            Locations.Any(x => string.Equals(x.Code, _pendingLocation, StringComparison.OrdinalIgnoreCase)))
        {
            Model.DefLocation = _pendingLocation;
        }

        _pendingSubClass = null;
        _pendingLocation = null;
    }

    private async Task LoadLookupsAsync()
    {
        var types = await Lookups.ListActiveTypesAsync();
        var classes = await Lookups.ListActiveClassesAsync();
        var classifications = await Lookups.ListClassificationsAsync();
        var uoms = await Lookups.ListActiveUomsAsync();
        var warehouses = await Lookups.ListActiveWarehousesAsync();

        Types = types.Succeeded ? types.Rows : [];
        Classes = classes.Succeeded ? classes.Rows : [];
        Classifications = classifications.Succeeded ? classifications.Rows : [];
        Uoms = uoms.Succeeded ? uoms.Rows : [];
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];

        if (!types.Succeeded || !classes.Succeeded || !classifications.Succeeded || !uoms.Succeeded || !warehouses.Succeeded)
        {
            ErrorMessage = types.ErrorMessage ?? classes.ErrorMessage ?? classifications.ErrorMessage
                ?? uoms.ErrorMessage ?? warehouses.ErrorMessage
                ?? "Unable to load lookups.";
        }
    }

    private async Task LoadSubClassesAsync(string? classCode)
    {
        SubClasses = [];
        if (string.IsNullOrWhiteSpace(classCode))
        {
            SubClassesLoading = false;
            return;
        }

        var version = Interlocked.Increment(ref _subClassLoadVersion);
        SubClassesLoading = true;
        var result = await Lookups.ListActiveSubClassesAsync(classCode);
        if (version != _subClassLoadVersion)
        {
            return;
        }

        SubClassesLoading = false;
        SubClasses = result.Succeeded ? result.Rows : [];
    }

    private async Task LoadLocationsAsync(string? warehouseCode)
    {
        Locations = [];
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            LocationsLoading = false;
            return;
        }

        var version = Interlocked.Increment(ref _locationLoadVersion);
        LocationsLoading = true;
        var result = await Lookups.ListActiveLocationsAsync(warehouseCode);
        if (version != _locationLoadVersion)
        {
            return;
        }

        LocationsLoading = false;
        Locations = result.Succeeded ? result.Rows : [];
    }

    private void CaptureCleanSnapshot() => _cleanSnapshot = Snapshot(Model);

    private IvStockMasterImageGalleryChangeSet? BuildGalleryChanges()
    {
        if (_pendingImages.Count == 0
            && _pendingImageRemovals.Count == 0
            && _requestedPrimaryExistingImageId is null
            && _requestedPrimaryPendingImageToken is null)
        {
            return null;
        }

        IvStockMasterPrimarySelection? selection = null;
        if (_requestedPrimaryExistingImageId is long existingImageId)
        {
            selection = new IvStockMasterPrimarySelection { ExistingImageId = existingImageId };
        }
        else if (_requestedPrimaryPendingImageToken is Guid pendingToken)
        {
            selection = new IvStockMasterPrimarySelection { PendingImageToken = pendingToken };
        }

        return new IvStockMasterImageGalleryChangeSet
        {
            Additions = _pendingImages.ToArray(),
            RemoveImageIds = _pendingImageRemovals.ToArray(),
            PrimarySelection = selection
        };
    }

    private void ClearPendingImageState()
    {
        _persistedImages.Clear();
        _pendingImages.Clear();
        _pendingImageRemovals.Clear();
        _selectedExistingImageId = null;
        _selectedPendingImageToken = null;
        _requestedPrimaryExistingImageId = null;
        _requestedPrimaryPendingImageToken = null;
        _imageError = null;
        _isPreparingImage = false;
        _imageInputKey++;
    }

    private async Task LoadGalleryAsync(string itemCode, bool clearPending)
    {
        var result = await StockMasterImages.ListAsync(itemCode);
        if (!result.Succeeded || result.Data is null)
        {
            _persistedImages.Clear();
            ErrorMessage = result.Message ?? "Unable to load item images.";
            return;
        }

        _persistedImages.Clear();
        _persistedImages.AddRange(result.Data.OrderBy(x => x.SortOrder));
        if (clearPending)
        {
            _pendingImages.Clear();
            _pendingImageRemovals.Clear();
            _requestedPrimaryExistingImageId = null;
            _requestedPrimaryPendingImageToken = null;
            _selectedExistingImageId = null;
            _selectedPendingImageToken = null;
        }
        else
        {
            _pendingImageRemovals.IntersectWith(_persistedImages.Select(x => x.Uid));
            if (_requestedPrimaryExistingImageId is long requestedId
                && _persistedImages.All(x => x.Uid != requestedId))
            {
                _requestedPrimaryExistingImageId = null;
            }

            if (_selectedExistingImageId is long selectedId
                && _persistedImages.All(x => x.Uid != selectedId))
            {
                _selectedExistingImageId = null;
            }
        }

        EnsureSelectedImage();
    }

    private bool IsImageAvailable(long imageId) =>
        !_pendingImageRemovals.Contains(imageId)
        && _persistedImages.Any(x => x.Uid == imageId);

    private bool IsImageAvailable(IvStockMasterImageRow image) => IsImageAvailable(image.Uid);

    private bool IsPrimaryImage(long imageId) => EffectivePrimaryPersisted?.Uid == imageId;

    private bool IsPrimaryImage(Guid token) => EffectivePrimaryPending?.Token == token;

    private bool IsSelectedImage(IvStockMasterImageRow image) => _selectedExistingImageId == image.Uid;

    private bool IsSelectedImage(Guid token) => _selectedPendingImageToken == token;

    private string ThumbnailClass(bool selected, bool primary) =>
        $"iv-stock-image-thumb{(selected ? " is-selected" : string.Empty)}{(primary ? " is-primary" : string.Empty)}";

    private string ThumbnailLabel(string label, bool primary) => primary ? $"{label}, Primary" : label;

    private string ImageThumbnailLabel(IvStockMasterImageRow image) =>
        ThumbnailLabel($"Image {image.SortOrder}", IsPrimaryImage(image.Uid));

    private string PendingImageThumbnailLabel(IvPendingStockImage image) =>
        ThumbnailLabel("New image", IsPrimaryImage(image.Token));

    private string? ThumbnailUrl(IvStockMasterImageRow image) => ImageUrl(image.Uid);

    private string PendingThumbnailUrl(IvPendingStockImage image) => ToDataUrl(image.Image);

    private string ImageUrl(long imageId) => Navigation.Resolve(
        $"/inventory/item-image?iCode={Uri.EscapeDataString(Model.ICode)}&imageId={imageId}");

    private static string ToDataUrl(IvPreparedStockImage image) =>
        $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Content)}";

    private IvPendingStockImage? FindPendingImage(Guid token) =>
        _pendingImages.FirstOrDefault(x => x.Token == token);

    private IvStockMasterImageRow? EffectivePrimaryPersisted
    {
        get
        {
            if (_requestedPrimaryExistingImageId is long requestedId)
            {
                return _persistedImages.FirstOrDefault(x => x.Uid == requestedId && IsImageAvailable(x.Uid));
            }

            if (_requestedPrimaryPendingImageToken is not null)
            {
                return null;
            }

            return _persistedImages.FirstOrDefault(x => x.IsPrimary && IsImageAvailable(x.Uid))
                ?? _persistedImages.Where(x => IsImageAvailable(x.Uid)).OrderBy(x => x.SortOrder).FirstOrDefault();
        }
    }

    private IvPendingStockImage? EffectivePrimaryPending
    {
        get
        {
            if (_requestedPrimaryPendingImageToken is Guid requestedToken)
            {
                return FindPendingImage(requestedToken);
            }

            if (_requestedPrimaryExistingImageId is not null
                || _persistedImages.Any(x => x.IsPrimary && IsImageAvailable(x.Uid))
                || _persistedImages.Any(x => IsImageAvailable(x.Uid)))
            {
                return null;
            }

            return _pendingImages.FirstOrDefault();
        }
    }

    private void EnsureSelectedImage()
    {
        if (_selectedExistingImageId is long existingId && IsImageAvailable(existingId))
        {
            return;
        }

        if (_selectedPendingImageToken is Guid pendingToken && FindPendingImage(pendingToken) is not null)
        {
            return;
        }

        _selectedExistingImageId = EffectivePrimaryPersisted?.Uid;
        _selectedPendingImageToken = EffectivePrimaryPending?.Token;
    }

    private static string Snapshot(IvStockMasterEditVm model) =>
        JsonSerializer.Serialize(model);

    private static IvStockMasterEditVm CreateBlank() =>
        new()
        {
            HasImage = false,
            MfgType = "BUY",
            IsActive = true,
            StockControl = true,
            ExpiryControl = IvExpiryControlModes.None
        };

    private static IvStockMasterEditVm Clone(IvStockMasterEditVm source) =>
        new()
        {
            ICode = source.ICode,
            IDesc = source.IDesc,
            HasImage = source.HasImage,
            MfgType = string.IsNullOrWhiteSpace(source.MfgType) ? "BUY" : source.MfgType,
            Barcode = source.Barcode,
            Brand = source.Brand,
            IsActive = source.IsActive,
            IType = source.IType,
            IClassCode = source.IClassCode,
            ISubClassCode = source.ISubClassCode,
            StdUom = source.StdUom,
            SellingUom = source.SellingUom,
            PurUom = source.PurUom,
            StockControl = source.StockControl,
            LotControl = source.LotControl,
            ExpiryControl = source.ExpiryControl,
            DefWarehouse = source.DefWarehouse,
            DefLocation = source.DefLocation,
            MinStock = source.MinStock,
            MaxStock = source.MaxStock,
            StdPackSize = source.StdPackSize,
            PurStdPackSize = source.PurStdPackSize,
            SellingPrice = source.SellingPrice,
            PurchasePrice = source.PurchasePrice,
            SellingGlCode = source.SellingGlCode,
            PurchaseGlCode = source.PurchaseGlCode,
            TaxGroup = source.TaxGroup,
            PurchaseTaxGroup = source.PurchaseTaxGroup,
            Classification = source.Classification,
            Size = source.Size,
            Color = source.Color,
            RowVersion = source.RowVersion,
            CreatedDate = source.CreatedDate,
            CreatedBy = source.CreatedBy,
            ModifiedDate = source.ModifiedDate,
            ModifiedBy = source.ModifiedBy
        };
}

public sealed record IvExpiryControlOption(string Code, string Label);
