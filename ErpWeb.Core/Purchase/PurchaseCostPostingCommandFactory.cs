using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Core.Purchase;

public sealed record PurchaseCostPostingRequest(
    string SourceDocumentType,
    string SourceDocumentId,
    string SourceDocumentNo,
    int CostingRevision,
    string PostingRole,
    DateTime EffectiveAt,
    object SourceSnapshot,
    string? ReasonCode = null,
    string? ReasonText = null,
    long? ReversesPostingId = null);

public interface IPurchaseCostPostingCommandFactory
{
    StockPostingCommand Create(PurchaseCostPostingRequest request);
}

public sealed class PurchaseCostPostingCommandFactory : IPurchaseCostPostingCommandFactory
{
    public StockPostingCommand Create(PurchaseCostPostingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CostingRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "CostingRevision cannot be negative.");

        var role = (request.PostingRole ?? string.Empty).Trim().ToUpperInvariant();
        if (role is not ("PRIMARY" or "REVERSAL"))
            throw new ArgumentException("PostingRole must be PRIMARY or REVERSAL.", nameof(request));

        var semantic = new
        {
            module = "PROCUREMENT",
            request.SourceDocumentType,
            request.SourceDocumentId,
            request.SourceDocumentNo,
            request.CostingRevision,
            role,
            request.EffectiveAt,
            request.ReversesPostingId,
            request.ReasonCode,
            request.ReasonText
        };
        var evidence = StockPostingFingerprint.Create(semantic, request.SourceSnapshot);
        return new StockPostingCommand
        {
            RequestId = RequestId(semantic),
            CommandType = $"PROCUREMENT_{request.SourceDocumentType}_{role}",
            SourceModule = "PROCUREMENT",
            SourceDocumentType = request.SourceDocumentType,
            SourceDocumentId = request.SourceDocumentId,
            SourceDocumentNo = request.SourceDocumentNo,
            DocumentRevision = request.CostingRevision,
            PostingRole = role,
            EffectiveAt = request.EffectiveAt,
            Evidence = evidence,
            ReversesPostingId = request.ReversesPostingId,
            ReasonCode = request.ReasonCode,
            ReasonText = request.ReasonText
        };
    }

    private static Guid RequestId(object semantic)
    {
        var canonical = StockPostingFingerprint.Canonicalize(
            System.Text.Json.JsonSerializer.Serialize(semantic));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
