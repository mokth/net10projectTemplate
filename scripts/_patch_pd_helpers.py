# Builds the Phase-2 PrProductDefService.cs
from pathlib import Path

helpers = Path(r"c:/wincom/net10projects/ErpWeb.Core/Planning/_helpers_tail.cs").read_text(encoding="utf-8")

# Patch MapEdit in helpers
old_map = '''            ProdCode = header.ProdCode,
            ProdDesc = product?.IDesc,
            StdUom = product?.StdUom,
            MfgType = PrMfgTypes.Normalize(product?.MfgType),
            IsActive = product?.IsActive ?? false,
            BomHdrId = header.Uid,
            Version = header.Version,
            Status = header.Status,
            EffectiveFrom = header.EffectiveFrom,
            EffectiveTo = header.EffectiveTo,
            BaseQty = header.BaseQty,
            BaseUom = header.BaseUom,
            Prefix = header.Prefix,
            Remark = header.Remark,
            HeaderRowVersion = header.RowVersion,
            Lines = lines.Select(x => new PrProductDefLineVm
            {
                OperationKey = x.OperationKey,
                Uid = x.Uid,
                ICode = x.ICode,
                IName = x.IName,
                StdQty = x.StdQty,
                StdUom = x.StdUom,
                SeqNo = x.SeqNo,
                ScrapPercent = x.ScrapPercent,
                Warehouse = x.Warehouse,
                BomDefault = x.BomDefault,
                AlternateGroupCode = x.AlternateGroupCode,
                Tolerance = x.Tolerance,
                IssueMethod = x.IssueMethod,
                SupplySource = x.SupplySource,
                ProducingRouteStepKey = x.ProducingRouteStepId is { } producerId
                                        && routeStepKeyById.TryGetValue(producerId, out var producerKey)
                    ? producerKey
                    : null,
                MfgType = mfgByCode.TryGetValue(x.ICode, out var m) ? m : PrMfgTypes.Buy
            }).ToList(),'''

new_map = '''            ProdCode = header.ProdCode,
            ProdDesc = product?.IDesc,
            StdUom = product?.StdUom,
            MfgType = PrMfgTypes.Normalize(product?.MfgType),
            IsActive = product?.IsActive ?? false,
            DefinitionCode = header.DefinitionCode,
            DefinitionName = header.DefinitionName,
            IsDefaultDefinition = header.IsDefaultDefinition,
            BomHdrId = header.Uid,
            Version = header.Version,
            Status = header.Status,
            BaseQty = header.BaseQty,
            BaseUom = header.BaseUom,
            Prefix = header.Prefix,
            Remark = header.Remark,
            HeaderRowVersion = header.RowVersion,
            Lines = lines.Select(x => new PrProductDefLineVm
            {
                OperationKey = x.OperationKey,
                Uid = x.Uid,
                ICode = x.ICode,
                IName = x.IName,
                StdQty = x.StdQty,
                StdUom = x.StdUom,
                SeqNo = x.SeqNo,
                ScrapPercent = x.ScrapPercent,
                Warehouse = x.Warehouse,
                BomDefault = x.BomDefault,
                AlternateGroupCode = x.AlternateGroupCode,
                Tolerance = x.Tolerance,
                IssueMethod = x.IssueMethod,
                SupplySource = x.SupplySource,
                ComponentDefinitionCode = x.ComponentDefinitionCode,
                ProducingRouteStepKey = x.ProducingRouteStepId is { } producerId
                                        && routeStepKeyById.TryGetValue(producerId, out var producerKey)
                    ? producerKey
                    : null,
                MfgType = mfgByCode.TryGetValue(x.ICode, out var m) ? m : PrMfgTypes.Buy
            }).ToList(),'''

assert old_map in helpers, "MapEdit block not found"
helpers = helpers.replace(old_map, new_map)

# Replace NormalizeCodes-only helpers with key-aware helpers at the end
old_norm = '''    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(NormalizeCode)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ValidateCode('''

new_norm = '''    private static List<PrProductDefinitionKey> NormalizeDefinitionKeys(IReadOnlyList<PrProductDefinitionKey>? keys)
    {
        var result = new List<PrProductDefinitionKey>();
        var seen = new HashSet<(string, string)>(new ProdDefTupleComparer());
        foreach (var key in keys ?? [])
        {
            var prod = NormalizeCode(key.ProdCode);
            var def = PrProductDefinitionCodes.Normalize(key.DefinitionCode);
            if (prod.Length == 0 || def.Length == 0)
                continue;
            if (!seen.Add((prod, def)))
                continue;
            result.Add(new PrProductDefinitionKey { ProdCode = prod, DefinitionCode = def });
        }

        return result;
    }

    private sealed class ProdDefTupleComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }

    private static string ValidateCode('''

assert old_norm in helpers
helpers = helpers.replace(old_norm, new_norm)

# Also add FailLookup helper before FailPage
old_fail = '''    private static IvMasterOperationResult<PrProductDefListPage> FailPage(ScopeError error) =>
        IvMasterOperationResult<PrProductDefListPage>.Fail(error.Code, error.Message);'''

new_fail = '''    private static IvMasterOperationResult<PrProductDefListPage> FailPage(ScopeError error) =>
        IvMasterOperationResult<PrProductDefListPage>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>> FailLookup(ScopeError error) =>
        IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(error.Code, error.Message);'''

assert old_fail in helpers
helpers = helpers.replace(old_fail, new_fail)

Path(r"c:/wincom/net10projects/ErpWeb.Core/Planning/_helpers_tail_patched.cs").write_text(helpers, encoding="utf-8")
print("patched helpers ok")
