using System.Globalization;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    internal static string ReferralPrescriptionSql(long patient)
    {
        if (patient < 0) throw new ArgumentOutOfRangeException(nameof(patient));
        var lower = patient - patient % 10;
        return "SELECT A.* FROM [紹介状追加] AS A INNER JOIN [紹介状] AS R ON A.[紹介番号] = R.[紹介番号] " +
            $"WHERE R.[カルテ番号] >= {lower.ToString(CultureInfo.InvariantCulture)} AND R.[カルテ番号] < {((decimal)lower + 10).ToString(CultureInfo.InvariantCulture)};";
    }

    internal static IReadOnlyDictionary<long, string> ParseReferralPrescriptions(
        IEnumerable<Dictionary<string, object?>> rows, IReadOnlyList<ReferralLetter> letters)
    {
        var result = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            object? Value(string field) => row.TryGetValue(field, out var value)
                ? value is DBNull ? null : value : throw new InvalidOperationException($"紹介状追加に{field}がありません。");
            string Text(string field) => Convert.ToString(Value(field), CultureInfo.CurrentCulture) ?? "";
            var id = ParseChartNumber(Text("紹介番号"));
            var letter = letters.SingleOrDefault(l => l.Number == id);
            if (letter == null || ParseChartNumber(Text("カルテ番号")) != letter.ChartNumber)
                throw new InvalidOperationException("紹介状追加の患者・紹介番号が一致しません。");
            // Match Form_Current: a non-null old disease field also selects the old format.
            bool legacy = Enumerable.Range(1, 10).Any(i => Value($"dr{i}") != null || Value($"ds{i}") != null || Value($"dt{i}") != null) ||
                Enumerable.Range(0, 15).Any(i => Value($"ill{i}") != null || Value($"itm{i}") != null);
            var lines = new List<string>();
            void Add(string name, string quantity, string unit)
            {
                var line = (name + (name.Length > 0 && (quantity.Length > 0 || unit.Length > 0) ? "　" : "") + quantity + unit).Trim();
                if (line.Length > 0) lines.Add(line);
            }
            if (legacy)
            {
                for (int i = 1; i <= 10; i++) Add(Text($"dr{i}"), Text($"ds{i}"), Text($"dt{i}"));
            }
            else
            {
                foreach (var line in Text("drg").ReplaceLineEndings("\n").Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    // Never silently discard ambiguous historical data.
                    if (parts.Length != 3) { lines.Add(line); continue; }
                    if (parts[1].Length > 0 && !decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.CurrentCulture, out _))
                        Add(parts[0] + " " + parts[1], "", parts[2]);
                    else Add(parts[0], parts[1], parts[2]);
                }
            }
            if (!result.TryAdd(id, string.Join("\r\n", lines)))
                throw new InvalidOperationException("紹介状追加の紹介番号が重複しています。");
        }
        return result;
    }
}
