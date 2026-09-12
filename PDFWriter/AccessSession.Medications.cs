using System.Globalization;
using System.Text;

namespace PDFWriter;

internal sealed record MedicationDay(string Key, string Header, string Text, string Notes = "", string Instructions = "", IReadOnlyList<MedicationRow>? Rows = null);
internal sealed record MedicationHistory(string PatientKey, string Status, IReadOnlyList<MedicationDay> Days);
internal sealed record MedicationRow(DateTime? Date, string Visit, long Number, decimal Order, string Name, string Quantity);

internal sealed partial class AccessSession
{
    private MedicationHistory? cachedMedication;
    private DateTime nextMedicationRead;

    private MedicationHistory ReadCachedMedication(object app, string id, string path, bool force)
    {
        var key = path + "|" + (ParseChartNumber(id) / 10).ToString(CultureInfo.InvariantCulture);
        if (!force && cachedMedication?.PatientKey == key && DateTime.UtcNow < nextMedicationRead) return cachedMedication;
        cachedMedication = null;
        MedicationHistory result;
        try { result = ReadMedicationHistory(app, id) with { PatientKey = key }; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new(key, $"薬歴を取得できません。（0x{ex.HResult:X8}）", []);
        }
        cachedMedication = result;
        nextMedicationRead = DateTime.UtcNow.AddSeconds(10);
        return result;
    }
    internal static string MedicationSql(string id)
    {
        long number = ParseChartNumber(id);
        long lower = number - number % 10;
        string from = lower.ToString(CultureInfo.InvariantCulture);
        string to = ((decimal)lower + 10).ToString(CultureInfo.InvariantCulture);
        // Numeric literals are generated only after integer validation. No form
        // references, period restriction, DISTINCT, or TOP: preserve all drug rows.
        return "SELECT V.[受診日], M.[受診コード], M.[カルテ番号], M.[順番], M.[薬名], M.[数量], " +
            "V.[カルテ番号] AS [受診患者番号] FROM [受診投薬] AS M LEFT JOIN [受診] AS V " +
            "ON M.[受診コード] = V.[受診コード] " +
            $"WHERE M.[カルテ番号] >= {from} AND M.[カルテ番号] < {to} " +
            $"AND (V.[カルテ番号] Is Null OR (V.[カルテ番号] >= {from} AND V.[カルテ番号] < {to})) " +
            "ORDER BY V.[受診日] DESC, M.[受診コード], M.[順番], M.[カルテ番号];";
    }
    internal static MedicationHistory ReadMedicationHistory(object app, string id, bool procedures = false)
    {
        var sql = MedicationSql(id);
        if (procedures) sql = sql.Replace("[受診投薬]", "[受診処置手術]").Replace("M.[薬名]", "M.[行為名] AS [薬名]");
        object? database = null, records = null;
        try
        {
            database = Step("薬歴用CurrentDbの取得", () => AccessDispatch.Call(app, "CurrentDb"));
            // DAO dbOpenSnapshot = 4, dbReadOnly = 4. Do not change live forms.
            records = Step("薬歴の読み取り", () => AccessDispatch.Call(database, "OpenRecordset", sql, 4, 4));
            return ReadMedicationRecords(records, ParseChartNumber(id));
        }
        finally
        {
            try
            {
                if (records != null)
                {
                    try { AccessDispatch.Call(records, "Close"); }
                    catch (System.Runtime.InteropServices.COMException) { }
                    finally { Release(records); }
                }
            }
            finally { Release(database); }
        }
    }
    internal static MedicationHistory ReadMedicationRecords(object records, long number)
    {
        var rows = new List<MedicationRow>();
        object? fields = null;
        try
        {
            if (!Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
            {
                fields = AccessDispatch.Get(records, "Fields");
                var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int count = Convert.ToInt32(AccessDispatch.Get(fields, "Count"));
                for (int i = 0; i < count; i++)
                {
                    object? field = null;
                    try
                    {
                        field = AccessDispatch.Get(fields, "Item", i);
                        indices[Convert.ToString(AccessDispatch.Get(field, "Name")) ?? ""] = i;
                    }
                    finally { Release(field); }
                }
                foreach (var name in new[] { "受診日", "受診コード", "カルテ番号", "順番", "薬名", "数量", "受診患者番号" })
                    if (!indices.ContainsKey(name)) throw new InvalidOperationException($"薬歴に{name}がありません。");
                while (!Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
                {
                    if (AccessDispatch.Call(records, "GetRows", 256) is not Array batch || batch.Rank != 2)
                        throw new InvalidOperationException("薬歴の形式を確認できません。");
                    if (batch.GetLength(1) < 256 && !Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
                        throw new InvalidOperationException("薬歴が読み取り中に変わりました。再取得してください。");
                    for (int row = batch.GetLowerBound(1); row <= batch.GetUpperBound(1); row++)
                    {
                        object? Read(string name)
                        {
                            var value = batch.GetValue(indices[name] + batch.GetLowerBound(0), row);
                            return value is DBNull ? null : value;
                        }
                        long patient = ParseChartNumber(Convert.ToString(Read("カルテ番号")) ?? "");
                        var visitPatient = Read("受診患者番号");
                        if (patient / 10 != number / 10 || (visitPatient != null && ParseChartNumber(Convert.ToString(visitPatient)!) / 10 != number / 10))
                            throw new InvalidOperationException("薬歴の患者番号が一致しません。");
                        var date = Read("受診日");
                        var order = Read("順番");
                        var quantity = Read("数量");
                        var name = Convert.ToString(Read("薬名"));
                        rows.Add(new(date == null ? null : Convert.ToDateTime(date).Date,
                            Convert.ToString(Read("受診コード")) ?? "", patient,
                            order == null ? decimal.MinValue : Convert.ToDecimal(order),
                            (string.IsNullOrWhiteSpace(name) ? "薬名未登録" : name).ReplaceLineEndings("\r\n"),
                            quantity == null ? "未登録" : Convert.ToString(quantity, CultureInfo.InvariantCulture) ?? "未登録"));
                    }
                }
            }
        }
        finally { Release(fields); }
        var days = new List<MedicationDay>();
        foreach (var group in rows.GroupBy(r => r.Date).OrderByDescending(g => g.Key))
        {
            string key = group.Key?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "日付不明";
            var text = new StringBuilder();
            foreach (var visit in group.GroupBy(r => (r.Visit, r.Number)))
            {
                if (text.Length > 0) text.Append("\r\n");
                text.Append($"受診 {visit.Key.Visit} · カルテ番号 {visit.Key.Number / 10}-{visit.Key.Number % 10}\r\n");
                foreach (var row in visit.OrderBy(r => r.Order)) text.Append($"{row.Name}　数量：{row.Quantity}\r\n");
            }
            days.Add(new(key, $"{key}（{group.Count()}件）", text.ToString().TrimEnd('\r', '\n'), Rows: group.ToArray()));
        }
        return new((number / 10).ToString(CultureInfo.InvariantCulture),
            rows.Count == 0 ? "薬歴はありません。" : $"{days.Count}日分 / {rows.Count}件 · 全枝番", days);
    }
}
