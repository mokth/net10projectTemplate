using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Purchase;

/// Future orphan-reconciliation job (out of scope): scan
/// {root}/{company}/{branch}/SUPPLIER/** and delete files with no matching POAttachFile.DocPath.
public sealed class PoSupplierAttachmentService : IPoSupplierAttachmentService
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxAttachmentsPerSupplier = 20;
    public const string DocKey = PoSupplierService.AttachDocKey;

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
    private readonly IPoSupplierRepository _suppliers;
    private readonly AttachmentStorageOptions _options;
    private readonly ILogger<PoSupplierAttachmentService> _logger;

    public PoSupplierAttachmentService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        IPoSupplierRepository suppliers,
        IOptions<AttachmentStorageOptions> options,
        ILogger<PoSupplierAttachmentService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _suppliers = suppliers;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PoSupplierAttachmentRow>>> ListAsync(
        string suppCode,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IReadOnlyList<PoSupplierAttachmentRow>>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Access, cancellationToken))
        {
            return Fail<IReadOnlyList<PoSupplierAttachmentRow>>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (suppCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<IReadOnlyList<PoSupplierAttachmentRow>>(IvMasterErrorCode.Validation, "Supplier code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await SupplierExistsAsync(db, context.CompanyCode!, context.BranchCode!, code, cancellationToken))
        {
            return Fail<IReadOnlyList<PoSupplierAttachmentRow>>(IvMasterErrorCode.NotFound, "Supplier was not found.");
        }

        var docId = SupplierAttachDocId.Compute(code);
        var rows = await db.PoAttachFiles
            .AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.DocKey == DocKey
                && x.DocId == docId)
            .OrderBy(x => x.DocName)
            .Select(x => new PoSupplierAttachmentRow
            {
                DocName = x.DocName,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<PoSupplierAttachmentRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<PoSupplierAttachmentRow>> UploadAsync(
        string suppCode,
        string originalFileName,
        string? contentType,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<PoSupplierAttachmentRow>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Add, cancellationToken))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (suppCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "Supplier code is required.");
        }

        if (contentLength <= 0)
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "Empty files are not allowed.");
        }

        if (contentLength > MaxFileBytes)
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, $"File exceeds the {MaxFileBytes / (1024 * 1024)} MB limit.");
        }

        var displayName = SanitizeDisplayName(originalFileName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "File name is required.");
        }

        var ext = Path.GetExtension(displayName);
        if (!AllowedExtensions.Contains(ext))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, $"File type '{ext}' is not allowed.");
        }

        if (!IsContentTypeAllowed(ext, contentType))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "Content type does not match the file extension.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await SupplierExistsAsync(db, context.CompanyCode!, context.BranchCode!, code, cancellationToken))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.NotFound, "Supplier was not found.");
        }

        var docId = SupplierAttachDocId.Compute(code);
        var count = await db.PoAttachFiles.CountAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == docId,
            cancellationToken);
        if (count >= MaxAttachmentsPerSupplier)
        {
            return Fail<PoSupplierAttachmentRow>(
                IvMasterErrorCode.Validation,
                $"A supplier may have at most {MaxAttachmentsPerSupplier} attachments.");
        }

        var duplicate = await db.PoAttachFiles.AnyAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == docId
                 && x.DocName == displayName,
            cancellationToken);
        if (duplicate)
        {
            return IvMasterOperationResult<PoSupplierAttachmentRow>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "An attachment with this file name already exists.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DocName"] = "An attachment with this file name already exists."
                });
        }

        var root = ResolveRoot();
        var relativeDir = Path.Combine(context.CompanyCode!, context.BranchCode!, DocKey, docId);
        var storageName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var relativePath = Path.Combine(relativeDir, storageName).Replace('\\', '/');
        var absoluteDir = Path.GetFullPath(Path.Combine(root, relativeDir));
        var absolutePath = Path.GetFullPath(Path.Combine(absoluteDir, storageName));

        if (!IsUnderRoot(root, absolutePath))
        {
            return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "Invalid storage path.");
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
                TryDeleteFile(tempPath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
                return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "Empty files are not allowed.");
            }

            if (length > MaxFileBytes)
            {
                TryDeleteFile(tempPath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
                return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, $"File exceeds the {MaxFileBytes / (1024 * 1024)} MB limit.");
            }

            await using (var probe = File.OpenRead(tempPath))
            {
                if (!await MatchesMagicBytesAsync(ext, probe, cancellationToken))
                {
                    TryDeleteFile(tempPath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
                    return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "File content does not match the declared type.");
                }
            }

            // StorageName is generated — never overwrite an existing physical file.
            if (File.Exists(absolutePath))
            {
                TryDeleteFile(tempPath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
                return Fail<PoSupplierAttachmentRow>(IvMasterErrorCode.Validation, "Storage collision. Retry upload.");
            }

            File.Move(tempPath, absolutePath);

            var entity = new PoAttachFile
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                DocId = docId,
                DocName = displayName,
                DocKey = DocKey,
                DocPath = relativePath,
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
                TryDeleteFile(absolutePath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
                return IvMasterOperationResult<PoSupplierAttachmentRow>.Fail(
                    IvMasterErrorCode.DuplicateKey,
                    "An attachment with this file name already exists.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["DocName"] = "An attachment with this file name already exists."
                    });
            }
            catch
            {
                TryDeleteFile(absolutePath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
                throw;
            }

            return IvMasterOperationResult<PoSupplierAttachmentRow>.Ok(new PoSupplierAttachmentRow
            {
                DocName = entity.DocName,
                CreatedDate = entity.CreatedDate,
                CreatedBy = entity.CreatedBy
            });
        }
        catch
        {
            TryDeleteFile(tempPath, context.CompanyCode!, context.BranchCode!, code, docId, displayName, relativePath, "upload");
            throw;
        }
    }

    public async Task<IvMasterOperationResult<(Stream Stream, string FileName, string ContentType)>> DownloadAsync(
        string suppCode,
        string docName,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<(Stream, string, string)>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Access, cancellationToken))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (suppCode ?? string.Empty).Trim();
        var name = SanitizeDisplayName(docName);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.Validation, "Supplier code and file name are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await SupplierExistsAsync(db, context.CompanyCode!, context.BranchCode!, code, cancellationToken))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.NotFound, "Supplier was not found.");
        }

        var docId = SupplierAttachDocId.Compute(code);
        var row = await db.PoAttachFiles.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == docId
                 && x.DocName == name,
            cancellationToken);

        if (row is null || string.IsNullOrWhiteSpace(row.DocPath))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.NotFound, "Attachment was not found.");
        }

        var root = ResolveRoot();
        var absolutePath = Path.GetFullPath(Path.Combine(root, row.DocPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, absolutePath) || !File.Exists(absolutePath))
        {
            return Fail<(Stream, string, string)>(IvMasterErrorCode.NotFound, "Attachment was not found.");
        }

        Stream stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var contentType = GuessContentType(Path.GetExtension(name));
        return IvMasterOperationResult<(Stream, string, string)>.Ok((stream, name, contentType));
    }

    public async Task<IvMasterOperationResult<object>> DeleteAsync(
        string suppCode,
        string docName,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<object>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Edit, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (suppCode ?? string.Empty).Trim();
        var name = SanitizeDisplayName(docName);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return Fail<object>(IvMasterErrorCode.Validation, "Supplier code and file name are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await SupplierExistsAsync(db, context.CompanyCode!, context.BranchCode!, code, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.NotFound, "Supplier was not found.");
        }

        var docId = SupplierAttachDocId.Compute(code);
        var row = await db.PoAttachFiles.FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode
                 && x.BranchCode == context.BranchCode
                 && x.DocKey == DocKey
                 && x.DocId == docId
                 && x.DocName == name,
            cancellationToken);

        if (row is null)
        {
            // Idempotent: already-removed row is not-found.
            return Fail<object>(IvMasterErrorCode.NotFound, "Attachment was not found.");
        }

        var docPath = row.DocPath;
        db.PoAttachFiles.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        await TryDeletePhysicalFileAsync(
            context.CompanyCode!,
            context.BranchCode!,
            code,
            docId,
            name,
            docPath,
            "delete",
            cancellationToken);

        return IvMasterOperationResult<object>.Ok();
    }

    public Task TryDeletePhysicalFileAsync(
        string companyCode,
        string branchCode,
        string suppCode,
        string docId,
        string docName,
        string? docPath,
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(docPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            var root = ResolveRoot();
            var absolutePath = Path.GetFullPath(Path.Combine(root, docPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnderRoot(root, absolutePath))
            {
                LogFsFailure(companyCode, branchCode, suppCode, docId, docName, docPath, operation,
                    new InvalidOperationException("Path escaped storage root."));
                return Task.CompletedTask;
            }

            if (!File.Exists(absolutePath))
            {
                // Missing physical file after DB delete is a cleanup condition, not a failure.
                return Task.CompletedTask;
            }

            File.Delete(absolutePath);
        }
        catch (Exception ex)
        {
            LogFsFailure(companyCode, branchCode, suppCode, docId, docName, docPath, operation, ex);
        }

        return Task.CompletedTask;
    }

    private async Task<bool> SupplierExistsAsync(
        AppDbContext db,
        string company,
        string branch,
        string code,
        CancellationToken cancellationToken) =>
        await _suppliers.GetByCodeAsync(db, company, branch, code, includeChildren: false, cancellationToken) is not null;

    private string ResolveRoot()
    {
        var configured = string.IsNullOrWhiteSpace(_options.RootPath)
            ? Path.Combine("App_Data", "attachments")
            : _options.RootPath;
        return Path.GetFullPath(configured);
    }

    private void TryDeleteFile(
        string absolutePath,
        string companyCode,
        string branchCode,
        string suppCode,
        string docId,
        string docName,
        string? docPath,
        string operation)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }
        catch (Exception ex)
        {
            LogFsFailure(companyCode, branchCode, suppCode, docId, docName, docPath, operation, ex);
        }
    }

    private void LogFsFailure(
        string companyCode,
        string branchCode,
        string suppCode,
        string docId,
        string docName,
        string? docPath,
        string operation,
        Exception ex)
    {
        _logger.LogError(
            ex,
            "Attachment filesystem failure. Company={CompanyCode} Branch={BranchCode} SuppCode={SuppCode} DocID={DocId} DocName={DocName} DocPath={DocPath} Operation={Operation}",
            companyCode,
            branchCode,
            suppCode,
            docId,
            docName,
            docPath,
            operation);
    }

    private (IvMasterErrorCode? ErrorCode, string? Error, string? CompanyCode, string? BranchCode, string? UserId) ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return (IvMasterErrorCode.InvalidScope, "Invalid company or branch context.", null, null, null);
        }

        return (null, null, scope.CompanyCode, scope.BranchCode, scope.UserId);
    }

    private static IvMasterOperationResult<T> Fail<T>(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<T>.Fail(code, message);

    private static string SanitizeDisplayName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var raw = fileName.Trim();
        // Reject traversal / absolute / unicode separators before Path.GetFileName strips them.
        if (raw.Contains("..", StringComparison.Ordinal)
            || raw.Contains('/', StringComparison.Ordinal)
            || raw.Contains('\\', StringComparison.Ordinal)
            || raw.Contains('\u2215')
            || raw.Contains('\uFF0F')
            || raw.IndexOfAny(['\u2028', '\u2029']) >= 0
            || Path.IsPathRooted(raw))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(raw);
        name = name.Replace('\0', '_');
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (name.Length > 200)
        {
            var ext = Path.GetExtension(name);
            var baseLen = Math.Max(1, 200 - ext.Length);
            name = Path.GetFileNameWithoutExtension(name);
            name = (name.Length > baseLen ? name[..baseLen] : name) + ext;
        }

        return name;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContentTypeAllowed(string ext, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            // Allow missing Content-Type; magic-byte check still applies.
            return true;
        }

        var type = contentType.Split(';')[0].Trim();
        return AllowedContentTypes.TryGetValue(ext, out var allowed)
               && allowed.Any(a => string.Equals(a, type, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> MatchesMagicBytesAsync(string ext, Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        stream.Position = 0;
        if (read < 2)
        {
            return false;
        }

        return ext.ToLowerInvariant() switch
        {
            ".pdf" => read >= 4 && header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46,
            ".png" => read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8,
            ".gif" => read >= 4 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46,
            ".docx" or ".xlsx" => header[0] == 0x50 && header[1] == 0x4B,
            ".txt" => !ContainsNul(header, read),
            _ => false
        };
    }

    private static bool ContainsNul(byte[] bytes, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string GuessContentType(string ext) =>
        AllowedContentTypes.TryGetValue(ext, out var types) ? types[0] : "application/octet-stream";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;
}
