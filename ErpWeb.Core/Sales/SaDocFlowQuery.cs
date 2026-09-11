using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>E3: the document kinds the flow panel understands. CN is not a ledger document type.</summary>
public static class SaDocFlowTypes
{
    public const string So = SaDocTypes.So;
    public const string Do = SaDocTypes.Do;
    public const string Inv = SaDocTypes.Inv;
    /// <summary>Credit / debit note. Not a <see cref="SaDocTypes"/> member — CNs are not allocated.</summary>
    public const string Cn = "CN";

    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "SO" or "SALESORDER" => So,
        "DO" or "DELIVERYORDER" => Do,
        "INV" or "INVOICE" => Inv,
        "CN" or "DN" or "CREDITNOTE" or "DEBITNOTE" => Cn,
        var other => other
    };
}

public sealed class SaDocFlowNode
{
    public string DocType { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public short Line { get; init; }
    public decimal Qty { get; init; }
    public decimal Amount { get; init; }
    public DateTime? DocDate { get; init; }
    public string? Status { get; init; }
    /// <summary>Human-readable edge label, e.g. <c>SO → DO</c> or <c>INV → CN (POSTED)</c>.</summary>
    public string Relationship { get; init; } = string.Empty;

    public string Display => Line > 0 ? $"{DocType} {DocNo} · line {Line}" : $"{DocType} {DocNo}";
}

public sealed class SaDocFlowResult
{
    public string DocType { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    /// <summary>Documents that fed this one.</summary>
    public IReadOnlyList<SaDocFlowNode> Upstream { get; init; } = [];
    /// <summary>Documents this one fed.</summary>
    public IReadOnlyList<SaDocFlowNode> Downstream { get; init; } = [];

    public bool HasAny => Upstream.Count > 0 || Downstream.Count > 0;
}

/// <summary>
/// E3 — read-only document-flow query over the <c>SaDocApplication</c> allocation ledger plus the
/// CN references (<c>SaCdn.InvNo</c> / <c>SaCdn.DoNo</c>, which are not ledger rows).
/// <b>Never mutates.</b>
/// </summary>
public interface ISaDocFlowQuery
{
    Task<SaDocFlowResult> QueryAsync(
        string docType,
        string docNo,
        CancellationToken cancellationToken = default);
}

public sealed class SaDocFlowQuery : ISaDocFlowQuery
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public SaDocFlowQuery(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<SaDocFlowResult> QueryAsync(
        string docType,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        var type = SaDocFlowTypes.Normalize(docType);
        var no = (docNo ?? string.Empty).Trim();
        var scope = _tenant.TryBranchScope();
        if (scope is null || no.Length == 0 || type.Length == 0)
        {
            return new SaDocFlowResult { DocType = type, DocNo = no };
        }

        var company = scope.CompanyCode;
        var branch = scope.BranchCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var upstream = new List<SaDocFlowNode>();
        var downstream = new List<SaDocFlowNode>();

        // ── Ledger: what fed this document ──────────────────────────────────────────────
        if (type is SaDocTypes.So or SaDocTypes.Do or SaDocTypes.Inv)
        {
            var inbound = await db.SaDocApplications.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.TargetDocType == type && x.TargetDocId == no)
                .Select(x => new
                {
                    x.SourceDocType,
                    x.SourceDocId,
                    x.SourceLineId,
                    x.AppliedQty,
                    x.AppliedAmount
                })
                .ToListAsync(cancellationToken);

            upstream.AddRange(inbound.Select(x => new SaDocFlowNode
            {
                DocType = x.SourceDocType,
                DocNo = x.SourceDocId,
                Line = x.SourceLineId,
                Qty = x.AppliedQty,
                Amount = x.AppliedAmount ?? 0m,
                Relationship = $"{x.SourceDocType} → {type}"
            }));

            var outbound = await db.SaDocApplications.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.SourceDocType == type && x.SourceDocId == no)
                .Select(x => new
                {
                    x.TargetDocType,
                    x.TargetDocId,
                    x.TargetLineId,
                    x.AppliedQty,
                    x.AppliedAmount
                })
                .ToListAsync(cancellationToken);

            downstream.AddRange(outbound.Select(x => new SaDocFlowNode
            {
                DocType = x.TargetDocType,
                DocNo = x.TargetDocId,
                Line = x.TargetLineId,
                Qty = x.AppliedQty,
                Amount = x.AppliedAmount ?? 0m,
                Relationship = $"{type} → {x.TargetDocType}"
            }));
        }

        // ── CN references (not ledger rows) ────────────────────────────────────────────
        if (type == SaDocFlowTypes.Cn)
        {
            var note = await db.SaCdns.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no)
                .Select(x => new { x.InvNo, x.DoNo })
                .FirstOrDefaultAsync(cancellationToken);

            if (note?.InvNo is { Length: > 0 } invNo)
            {
                upstream.Add(new SaDocFlowNode
                {
                    DocType = SaDocTypes.Inv,
                    DocNo = invNo,
                    Relationship = $"{SaDocTypes.Inv} → CN (reference)"
                });
            }

            if (note?.DoNo is { Length: > 0 } doNo)
            {
                upstream.Add(new SaDocFlowNode
                {
                    DocType = SaDocTypes.Do,
                    DocNo = doNo,
                    Relationship = $"{SaDocTypes.Do} → CN (reference)"
                });
            }
        }
        else if (type == SaDocTypes.Inv)
        {
            var notes = await db.SaCdns.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.InvNo == no
                    && x.Type == SaCdnTypes.CreditNote)
                .Select(x => new { x.DocNo, x.Status, x.TotAmnt, x.DocDate })
                .ToListAsync(cancellationToken);

            downstream.AddRange(notes.Select(x => new SaDocFlowNode
            {
                DocType = SaDocFlowTypes.Cn,
                DocNo = x.DocNo,
                Amount = x.TotAmnt,
                DocDate = x.DocDate,
                Status = x.Status,
                Relationship = $"{SaDocTypes.Inv} → CN ({x.Status})"
            }));
        }

        await EnrichAsync(db, company, branch, upstream, cancellationToken);
        await EnrichAsync(db, company, branch, downstream, cancellationToken);

        return new SaDocFlowResult
        {
            DocType = type,
            DocNo = no,
            Upstream = upstream
                .OrderBy(x => x.DocType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DocNo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Line)
                .ToList(),
            Downstream = downstream
                .OrderBy(x => x.DocType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DocNo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Line)
                .ToList()
        };
    }

    /// <summary>Fills in the header date/status for SO / DO / INV nodes, in three batched queries.</summary>
    private static async Task EnrichAsync(
        AppDbContext db,
        string company,
        string branch,
        List<SaDocFlowNode> nodes,
        CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var soNos = nodes.Where(x => x.DocType == SaDocTypes.So).Select(x => x.DocNo).Distinct().ToList();
        var doNos = nodes.Where(x => x.DocType == SaDocTypes.Do).Select(x => x.DocNo).Distinct().ToList();
        var invNos = nodes.Where(x => x.DocType == SaDocTypes.Inv).Select(x => x.DocNo).Distinct().ToList();

        var soInfo = soNos.Count == 0
            ? []
            : await db.SaSos.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsCurrent && soNos.Contains(x.SoNo))
                .Select(x => new { x.SoNo, x.SoDate, x.Status })
                .ToListAsync(cancellationToken);
        var doInfo = doNos.Count == 0
            ? []
            : await db.SaDos.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && doNos.Contains(x.DoNo))
                .Select(x => new { x.DoNo, x.DoDate, x.Status })
                .ToListAsync(cancellationToken);
        var invInfo = invNos.Count == 0
            ? []
            : await db.SaInvoices.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && invNos.Contains(x.InvNo))
                .Select(x => new { x.InvNo, x.InvDate, x.Status })
                .ToListAsync(cancellationToken);

        var soByNo = soInfo.ToDictionary(x => x.SoNo, StringComparer.OrdinalIgnoreCase);
        var doByNo = doInfo.ToDictionary(x => x.DoNo, StringComparer.OrdinalIgnoreCase);
        var invByNo = invInfo.ToDictionary(x => x.InvNo, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            DateTime? date = node.DocDate;
            string? status = node.Status;

            if (node.DocType == SaDocTypes.So && soByNo.TryGetValue(node.DocNo, out var so))
            {
                date ??= so.SoDate;
                status ??= so.Status;
            }
            else if (node.DocType == SaDocTypes.Do && doByNo.TryGetValue(node.DocNo, out var dov))
            {
                date ??= dov.DoDate;
                status ??= dov.Status;
            }
            else if (node.DocType == SaDocTypes.Inv && invByNo.TryGetValue(node.DocNo, out var inv))
            {
                date ??= inv.InvDate;
                status ??= inv.Status;
            }

            nodes[i] = new SaDocFlowNode
            {
                DocType = node.DocType,
                DocNo = node.DocNo,
                Line = node.Line,
                Qty = node.Qty,
                Amount = node.Amount,
                DocDate = date,
                Status = status,
                Relationship = node.Relationship
            };
        }
    }
}
