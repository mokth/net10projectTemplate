namespace ErpWeb.Core.Inventory;

public static class IvLotNumberGenerator
{
    public const int AutoGenerateMaxSeq = 999;

    public static async Task<IReadOnlyList<string>> AllocateAsync(
        int count,
        string prefixOrFirstLot,
        int startSeq,
        bool autoGenerate,
        IReadOnlyCollection<string> usedInDocument,
        Func<string, Task<bool>> existsInDatabaseAsync)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ArgumentNullException.ThrowIfNull(existsInDatabaseAsync);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (usedInDocument is not null)
        {
            foreach (var value in usedInDocument)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    used.Add(value.Trim());
                }
            }
        }

        var allocated = new List<string>(count);
        if (autoGenerate)
        {
            var prefix = (prefixOrFirstLot ?? string.Empty).Trim();
            if (prefix.Length == 0)
            {
                prefix = DateTime.Today.ToString("yyMMdd");
            }

            var seq = startSeq < 1 ? 1 : startSeq;
            while (allocated.Count < count)
            {
                if (seq > AutoGenerateMaxSeq)
                {
                    throw new InvalidOperationException(
                        $"Unable to allocate {count} lot number(s); sequence exceeded {AutoGenerateMaxSeq}.");
                }

                var candidate = $"{prefix}{seq:000}";
                seq++;
                if (used.Contains(candidate))
                {
                    continue;
                }

                if (await existsInDatabaseAsync(candidate))
                {
                    continue;
                }

                used.Add(candidate);
                allocated.Add(candidate);
            }

            return allocated;
        }

        var manual = (prefixOrFirstLot ?? string.Empty).Trim();
        if (manual.Length == 0)
        {
            throw new InvalidOperationException("Manual lot number is required when auto generate is off.");
        }

        while (allocated.Count < count)
        {
            if (!used.Contains(manual) && !await existsInDatabaseAsync(manual))
            {
                used.Add(manual);
                allocated.Add(manual);
            }

            manual = NextManualLot(manual);
        }

        return allocated;
    }

    internal static string NextManualLot(string current)
    {
        var value = (current ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "-2";
        }

        var digitStart = value.Length;
        while (digitStart > 0 && char.IsDigit(value[digitStart - 1]))
        {
            digitStart--;
        }

        if (digitStart < value.Length)
        {
            var prefix = value[..digitStart];
            var digitPart = value[digitStart..];
            if (long.TryParse(digitPart, out var number))
            {
                var next = number + 1;
                var width = digitPart.Length;
                var nextText = next.ToString();
                if (nextText.Length < width)
                {
                    nextText = nextText.PadLeft(width, '0');
                }

                return prefix + nextText;
            }
        }

        var dash = value.LastIndexOf('-');
        if (dash > 0 && dash < value.Length - 1
            && int.TryParse(value[(dash + 1)..], out var suffix))
        {
            return $"{value[..dash]}-{suffix + 1}";
        }

        return $"{value}-2";
    }
}
