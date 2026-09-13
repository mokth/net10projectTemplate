using System.Security.Cryptography;
using System.Text;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Single source of truth for POAttachFile.DocID derived from SuppCode.
/// DocID = lowercase(hex(SHA256(UTF8(suppCode))))[..40]
/// </summary>
public static class SupplierAttachDocId
{
    public static string Compute(string suppCode)
    {
        ArgumentNullException.ThrowIfNull(suppCode);
        var trimmed = suppCode.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexString(hash).ToLowerInvariant()[..40];
    }
}
