using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Purchase;

public sealed class PoOrderAttachmentService : IPoOrderAttachmentService
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxAttachmentsPerDoc = 20;
    public const string DocKey = "PO";
    public const string TempDocIdPrefix = "POTMP-";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".docx", ".xlsx", ".txt"
    };

    private static readonly Dictionary<string, string[]> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ["application/pdf"],
        [".png"] = ["image/png"],
        [".jpg"] = ["image/jpeg"],
        [".jpeg"] = ["image/jpeg"],
        [".gif"] = ["image/gif"],
        [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip"],
        [".xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/zip"],
        [".txt"] = ["text/plain"]
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly AttachmentStorageOptions _options;
    private readonly PoOrderOptions _orderOptions;
    private readonly ILogger<PoOrderAttachmentService> _logger;

    public PoOrderAttachmentService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        IOptions<AttachmentStorageOptions> options,
        IOptions<PoOrderOptions> orderOptions,
        ILogger<PoOrderAttachmentService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _options = options.Value;
        _orderOptions = orderOptions.Value;
        _logger = logger;
    }

    public async Task MoveDraftToPoAsync(
        string company,
        string branch,
        string tempDocId,
        string poNo,
        CancellationToken cancellationToken = default)
    {
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var tempId = (tempDocId ?? string.Empty).Trim();
        var targetId = (poNo ?? string.Empty).Trim();
        if (companyCode.Length == 0 || branchCode.Length == 0 || tempId.Length == 0 || targetId.Length == 0)
        {
            return;
        }

        if (string.Equals(tempId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var root = ResolveRoot();
            var sourceDir = Path.GetFullPath(Path.Combine(root, companyCode, branchCode, DocKey, tempId));
            var targetDir = Path.GetFullPath(Path.Combine(root, companyCode, branchCode, DocKey, targetId));
            if (!IsUnderRoot(root, sourceDir) || !IsUnderRoot(root, targetDir))
            {
                _logger.LogWarning(
                    "PO attachment move skipped due to path escape. Company={Company} Branch={Branch} Temp={Temp} PoNo={PoNo}",
                    companyCode,
                    branchCode,
                    tempId,
                    targetId);
                return;
            }

            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var dest = Path.Combine(targetDir, name);
                try
                {
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }

                    File.Move(file, dest);
                }
                catch (Exception ex)
                {
                    LogFsFailure(companyCode, branchCode, tempId, name, file, "move", ex);
                }
            }

            TryDeleteEmptyDirectory(sourceDir);
        }
        catch (Exception ex)
        {
            LogFsFailure(companyCode, branchCode, tempId, targetId, null, "move", ex);
        }

        await Task.CompletedTask;
    }

    public async Task DiscardDraftAsync(string tempDocId, CancellationToken cancellationToken = default)
    {
        var tempId = (tempDocId ?? string.Empty).Trim();
        if (!IsTempDocId(tempId))
        {
            return;
        }

        var scope = _tenant.TryWriteScope() ?? _tenant.TryBranchScope();
        if (scope is null)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PoAttachFiles
            .Where(x =>
                x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.DocKey == DocKey
                && x.DocId == tempId
                && x.CreatedBy == Truncate(scope.UserId, 20))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            TryDeleteDocFolder(scope.CompanyCode, scope.BranchCode!, tempId);
            return;
        }

        var files = rows.Select(x => (x.DocName, x.DocPath)).ToList();
        db.PoAttachFiles.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        await DeletePhysicalForDocAsync(scope.CompanyCode, scope.BranchCode!, tempId, files, cancellationToken);
        TryDeleteDocFolder(scope.CompanyCode, scope.BranchCode!, tempId);
    }

    public Task DeletePhysicalForDocAsync(
        string company,
        string branch,
        string docId,
        IReadOnlyList<(string DocName, string? DocPath)> files,
        CancellationToken cancellationToken = default)
    {
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var id = (docId ?? string.Empty).Trim();
        if (companyCode.Length == 0 || branchCode.Length == 0 || id.Length == 0)
        {
            return Task.CompletedTask;
        }

        var root = ResolveRoot();
        foreach (var file in files ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.DocPath))
            {
                continue;
            }

            try
            {
                var absolutePath = Path.GetFullPath(
                    Path.Combine(root, companyCode, branchCode, DocKey, id, file.DocPath));
                if (!IsUnderRoot(root, absolutePath))
                {
                    LogFsFailure(companyCode, branchCode, id, file.DocName, file.DocPath, "delete",
                        new InvalidOperationException("Path escaped storage root."));
                    continue;
                }

                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                }
            }
            catch (Exception ex)
            {
                LogFsFailure(companyCode, branchCode, id, file.DocName, file.DocPath, "delete", ex);
            }
        }

        TryDeleteDocFolder(companyCode, branchCode, id);
        return Task.CompletedTask;
    }

    public async Task CleanupExpiredDraftsAsync(CancellationToken cancellationToken = default)
    {
        var hours = Math.Max(1, _orderOptions.DraftAttachmentTtlHours);
        var cutoff = _dates.Now.AddHours(-hours);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var expired = await db.PoAttachFiles
            .Where(x =>
                x.DocKey == DocKey
                && x.DocId.StartsWith(TempDocIdPrefix)
                && x.CreatedDate != null
                && x.CreatedDate < cutoff)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return;
        }

        var draftRows = expired.Where(x => IsTempDocId(x.DocId)).ToList();
        var groups = draftRows
            .GroupBy(x => new { x.CompanyCode, x.BranchCode, x.DocId })
            .ToList();

        db.PoAttachFiles.RemoveRange(draftRows);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var group in groups)
        {
            var files = group.Select(x => (x.DocName, x.DocPath)).ToList();
            await DeletePhysicalForDocAsync(
                group.Key.CompanyCode,
                group.Key.BranchCode,
                group.Key.DocId,
                files,
                cancellationToken);
        }
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PoOrderAttachmentRow>>> ListAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IReadOnlyList<PoOrderAttachmentRow>>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Access, cancellationToken))
        {
            return Fail<IReadOnlyList<PoOrderAttachmentRow>>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var id = (docId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            return Fail<IReadOnlyList<PoOrderAttachmentRow>>(IvMasterErrorCode.Validation, "Document id is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await AuthorizeDocAccessAsync(db, context, id, requireOwnerForTemp: true, cancellationToken);
        if (access is not null)
        {
            return Fail<IReadOnlyList<PoOrderAttachmentRow>>(access.Value.Code, access.Value.Message);
        }

        var rows = await db.PoAttachFiles
            .AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.DocKey == DocKey
                && x.DocId == id)
            .OrderBy(x => x.DocName)
            .Select(x => new PoOrderAttachmentRow
            {
                DocName = x.DocName,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<PoOrderAttachmentRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<PoOrderAttachmentRow>> UploadAsync(
        string docId,
        string originalFileName,
        string? contentType,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.ErrorCode is not null)
        {
            return Fail<PoOrderAttachmentRow>(context.ErrorCode.Value, context.Error!);
        }

        var canAdd = await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Add, cancellationToken);
        var canEdit = await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Edit, cancellationToken);
        if (!canAdd && !canEdit)
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var id = (docId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "Document id is required.");
        }

        if (contentLength <= 0)
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "Empty files are not allowed.");
        }

        if (contentLength > MaxFileBytes)
        {
            return Fail<PoOrderAttachmentRow>(
                IvMasterErrorCode.Validation,
                $"File exceeds the {MaxFileBytes / (1024 * 1024)} MB limit.");
        }

        var displayName = SanitizeDisplayName(originalFileName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "File name is required.");
        }

        var ext = Path.GetExtension(displayName);
        if (!AllowedExtensions.Contains(ext))
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, $"File type '{ext}' is not allowed.");
        }

        if (!IsContentTypeAllowed(ext, contentType))
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "Content type does not match the file extension.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await AuthorizeDocAccessAsync(db, context, id, requireOwnerForTemp: true, cancellationToken);
        if (access is not null)
        {
            return Fail<PoOrderAttachmentRow>(access.Value.Code, access.Value.Message);
        }

        var count = await db.PoAttachFiles.CountAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == id,
            cancellationToken);
        if (count >= MaxAttachmentsPerDoc)
        {
            return Fail<PoOrderAttachmentRow>(
                IvMasterErrorCode.Validation,
                $"A purchase order may have at most {MaxAttachmentsPerDoc} attachments.");
        }

        var duplicate = await db.PoAttachFiles.AnyAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == id
                 && x.DocName == displayName,
            cancellationToken);
        if (duplicate)
        {
            return IvMasterOperationResult<PoOrderAttachmentRow>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "An attachment with this file name already exists.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DocName"] = "An attachment with this file name already exists."
                });
        }

        var root = ResolveRoot();
        var relativeDir = Path.Combine(context.CompanyCode!, context.BranchCode!, DocKey, id);
        var storageName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var absoluteDir = Path.GetFullPath(Path.Combine(root, relativeDir));
        var absolutePath = Path.GetFullPath(Path.Combine(absoluteDir, storageName));
        if (!IsUnderRoot(root, absolutePath))
        {
            return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "Invalid storage path.");
        }

        Directory.CreateDirectory(absoluteDir);
        var tempPath = absolutePath + ".tmp";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(fs, cancellationToken);
            }

            var length = new FileInfo(tempPath).Length;
            if (length <= 0)
            {
                TryDeleteFile(tempPath);
                return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "Empty files are not allowed.");
            }

            if (length > MaxFileBytes)
            {
                TryDeleteFile(tempPath);
                return Fail<PoOrderAttachmentRow>(
                    IvMasterErrorCode.Validation,
                    $"File exceeds the {MaxFileBytes / (1024 * 1024)} MB limit.");
            }

            await using (var probe = File.OpenRead(tempPath))
            {
                if (!await MatchesMagicBytesAsync(ext, probe, cancellationToken))
                {
                    TryDeleteFile(tempPath);
                    return Fail<PoOrderAttachmentRow>(
                        IvMasterErrorCode.Validation,
                        "File content does not match the declared type.");
                }
            }

            if (File.Exists(absolutePath))
            {
                TryDeleteFile(tempPath);
                return Fail<PoOrderAttachmentRow>(IvMasterErrorCode.Validation, "Storage collision. Retry upload.");
            }

            File.Move(tempPath, absolutePath);

            var entity = new PoAttachFile
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                DocId = id,
                DocName = displayName,
                DocKey = DocKey,
                DocPath = storageName,
                CreatedDate = _dates.Now,
                CreatedBy = Truncate(context.UserId!, 20)
            };

            db.PoAttachFiles.Add(entity);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                TryDeleteFile(absolutePath);
                return IvMasterOperationResult<PoOrderAttachmentRow>.Fail(
                    IvMasterErrorCode.DuplicateKey,
                    "An attachment with this file name already exists.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["DocName"] = "An attachment with this file name already exists."
                    });
            }
            catch
            {
                TryDeleteFile(absolutePath);
                throw;
            }

            return IvMasterOperationResult<PoOrderAttachmentRow>.Ok(new PoOrderAttachmentRow
            {
                DocName = entity.DocName,
                CreatedDate = entity.CreatedDate,
                CreatedBy = entity.CreatedBy
            });
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public async Task<IvMasterOperationResult<(Stream Stream, string FileName, string ContentType)>> DownloadAsync(
        string docId,
        string docName,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<(Stream, string, string)>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Access, cancellationToken))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var id = (docId ?? string.Empty).Trim();
        var name = (docName ?? string.Empty).Trim();
        if (id.Length == 0 || name.Length == 0)
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.Validation, "Document id and file name are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await AuthorizeDocAccessAsync(db, context, id, requireOwnerForTemp: true, cancellationToken);
        if (access is not null)
        {
            return Fail<(Stream, string, string)>(access.Value.Code, access.Value.Message);
        }

        var row = await db.PoAttachFiles.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == id
                 && x.DocName == name,
            cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.DocPath))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.NotFound, "Attachment was not found.");
        }

        var root = ResolveRoot();
        var absolutePath = Path.GetFullPath(
            Path.Combine(root, context.CompanyCode!, context.BranchCode!, DocKey, id, row.DocPath));
        if (!IsUnderRoot(root, absolutePath) || !File.Exists(absolutePath))
        {
            absolutePath = TryResolveLeftoverTempPath(
                context.CompanyCode!,
                context.BranchCode!,
                id,
                row.DocName,
                row.DocPath,
                root);
        }

        if (absolutePath is null || !File.Exists(absolutePath))
        {
            return Fail<(Stream, string, string)>(
                IvMasterErrorCode.NotFound,
                "Attachment file is not yet available. Contact operations.");
        }

        Stream stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var contentType = ResolveContentType(Path.GetExtension(row.DocName));
        return IvMasterOperationResult<(Stream, string, string)>.Ok((stream, row.DocName, contentType));
    }

    public async Task<IvMasterOperationResult<object>> DeleteAsync(
        string docId,
        string docName,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.ErrorCode is not null)
        {
            return Fail<object>(context.ErrorCode.Value, context.Error!);
        }

        var canAdd = await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Add, cancellationToken);
        var canEdit = await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Edit, cancellationToken);
        if (!canAdd && !canEdit)
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var id = (docId ?? string.Empty).Trim();
        var name = (docName ?? string.Empty).Trim();
        if (id.Length == 0 || name.Length == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "Document id and file name are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await AuthorizeDocAccessAsync(db, context, id, requireOwnerForTemp: true, cancellationToken);
        if (access is not null)
        {
            return Fail<object>(access.Value.Code, access.Value.Message);
        }

        var row = await db.PoAttachFiles.FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == id
                 && x.DocName == name,
            cancellationToken);
        if (row is null)
        {
            return Fail<object>(IvMasterErrorCode.NotFound, "Attachment was not found.");
        }

        var files = new List<(string DocName, string? DocPath)> { (row.DocName, row.DocPath) };
        db.PoAttachFiles.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        await DeletePhysicalForDocAsync(context.CompanyCode!, context.BranchCode!, id, files, cancellationToken);
        return IvMasterOperationResult<object>.Ok();
    }

    private string? TryResolveLeftoverTempPath(
        string companyCode,
        string branchCode,
        string poNo,
        string docName,
        string docPath,
        string root)
    {
        try
        {
            var poRoot = Path.GetFullPath(Path.Combine(root, companyCode, branchCode, DocKey));
            if (!IsUnderRoot(root, poRoot) || !Directory.Exists(poRoot))
            {
                return null;
            }

            foreach (var tempDir in Directory.EnumerateDirectories(poRoot, TempDocIdPrefix + "*"))
            {
                var tempPath = Path.GetFullPath(Path.Combine(tempDir, docPath));
                if (!IsUnderRoot(root, tempPath))
                {
                    LogFsFailure(companyCode, branchCode, poNo, docName, docPath, "download-temp-fallback",
                        new InvalidOperationException("Path escaped storage root."));
                    continue;
                }

                if (File.Exists(tempPath))
                {
                    return tempPath;
                }
            }
        }
        catch (Exception ex)
        {
            LogFsFailure(companyCode, branchCode, poNo, docName, docPath, "download-temp-fallback", ex);
        }

        return null;
    }

    private async Task<(IvMasterErrorCode Code, string Message)?> AuthorizeDocAccessAsync(
        AppDbContext db,
        UserContext context,
        string docId,
        bool requireOwnerForTemp,
        CancellationToken cancellationToken)
    {
        if (IsTempDocId(docId))
        {
            if (!requireOwnerForTemp)
            {
                return null;
            }

            var owned = await db.PoAttachFiles.AsNoTracking().AnyAsync(
                x => x.CompanyCode == context.CompanyCode
                     && x.BranchCode == context.BranchCode
                     && x.DocKey == DocKey
                     && x.DocId == docId
                     && x.CreatedBy == Truncate(context.UserId!, 20),
                cancellationToken);
            if (owned)
            {
                return null;
            }

            var any = await db.PoAttachFiles.AsNoTracking().AnyAsync(
                x => x.CompanyCode == context.CompanyCode
                     && x.BranchCode == context.BranchCode
                     && x.DocKey == DocKey
                     && x.DocId == docId,
                cancellationToken);
            return any
                ? (IvMasterErrorCode.AccessDenied, "Not authorized for this draft attachment.")
                : null;
        }

        var header = await db.PoOrders.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.PoNo == docId,
            cancellationToken);
        if (header is null)
        {
            return (IvMasterErrorCode.NotFound, "Purchase Order was not found.");
        }

        if (_orderOptions.SelfViewEdit
            && !string.Equals(header.CreatedBy, Truncate(context.UserId!, 20), StringComparison.OrdinalIgnoreCase))
        {
            return (IvMasterErrorCode.AccessDenied, "Not authorized for this Purchase Order.");
        }

        return null;
    }

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company or branch context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.UserId);
    }

    private UserContext ValidateWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.UserId);
    }

    private string ResolveRoot()
    {
        var configured = string.IsNullOrWhiteSpace(_options.RootPath)
            ? Path.Combine("App_Data", "attachments")
            : _options.RootPath;
        return Path.GetFullPath(configured);
    }

    private void TryDeleteDocFolder(string company, string branch, string docId)
    {
        try
        {
            var root = ResolveRoot();
            var dir = Path.GetFullPath(Path.Combine(root, company, branch, DocKey, docId));
            if (!IsUnderRoot(root, dir) || !Directory.Exists(dir))
            {
                return;
            }

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch (Exception ex)
        {
            LogFsFailure(company, branch, docId, null, null, "cleanup", ex);
        }
    }

    private static void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteFile(string absolutePath)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }
        catch
        {
            // best effort
        }
    }

    private void LogFsFailure(
        string companyCode,
        string branchCode,
        string docId,
        string? docName,
        string? docPath,
        string operation,
        Exception ex)
    {
        _logger.LogWarning(
            ex,
            "PO attachment filesystem failure. Company={CompanyCode} Branch={BranchCode} DocID={DocId} DocName={DocName} DocPath={DocPath} Operation={Operation}",
            companyCode,
            branchCode,
            docId,
            docName,
            docPath,
            operation);
    }

    private static bool IsTempDocId(string docId) =>
        docId.StartsWith(TempDocIdPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderRoot(string root, string absolutePath)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var pathFull = Path.GetFullPath(absolutePath);
        return pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDisplayName(string? originalFileName)
    {
        var name = Path.GetFileName(originalFileName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static bool IsContentTypeAllowed(string ext, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        if (!AllowedContentTypes.TryGetValue(ext, out var allowed))
        {
            return false;
        }

        var ct = contentType.Split(';', 2)[0].Trim();
        return allowed.Any(x => string.Equals(x, ct, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> MatchesMagicBytesAsync(string ext, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read <= 0)
        {
            return false;
        }

        return ext.ToLowerInvariant() switch
        {
            ".pdf" => read >= 4 && buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46,
            ".png" => read >= 8 && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47,
            ".jpg" or ".jpeg" => read >= 3 && buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF,
            ".gif" => read >= 4 && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38,
            ".docx" or ".xlsx" => read >= 4 && buffer[0] == 0x50 && buffer[1] == 0x4B,
            ".txt" => true,
            _ => false
        };
    }

    private static string ResolveContentType(string ext) =>
        AllowedContentTypes.TryGetValue(ext, out var types) && types.Length > 0
            ? types[0]
            : "application/octet-stream";

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;

    private static IvMasterOperationResult<T> Fail<T>(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<T>.Fail(code, message);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private readonly record struct UserContext(
        IvMasterErrorCode? ErrorCode,
        string? Error,
        string? CompanyCode,
        string? BranchCode,
        string? UserId)
    {
        public static UserContext Fail(IvMasterErrorCode code, string error) =>
            new(code, error, null, null, null);

        public static UserContext Ok(string company, string branch, string user) =>
            new(null, null, company, branch, user);
    }
}
