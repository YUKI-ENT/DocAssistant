using System.Globalization;
using System.Text;

namespace PDFWriter;

internal sealed record AccessNotes(string Status, string Text = "", string Instructions = "", IReadOnlyList<AccessNote>? Notes = null, IReadOnlyList<AccessNote>? Orders = null);
internal sealed record AccessNote(DateTime? Date, string Visit, long ChartNumber, decimal Order, string Text);

internal sealed partial class AccessSession
{
    private string notesKey = "";
    private DateTime nextNotesRead;
    private AccessNotes? cachedNotes;

    private AccessNotes ReadCachedNotes(object controls, string id, string path, bool force)
    {
        string key = path + "\n" + id;
        if (!force && key == notesKey && cachedNotes != null && DateTime.UtcNow < nextNotesRead) return cachedNotes;
        // A failed read cannot leave a previous patient's notes in the cache.
        cachedNotes = null;
        var result = ReadClinicalNotes(controls, id);
        notesKey = key;
        nextNotesRead = DateTime.UtcNow.AddSeconds(5);
        cachedNotes = result;
        return result;
    }
    internal static AccessNotes ReadClinicalNotes(object controls, string id) => ReadSubformNotes(controls, id, false);

    internal static AccessNotes ReadDraftNotes(object controls, string id) => ReadSubformNotes(controls, id, true);

    private static AccessNotes ReadSubformNotes(object controls, string id, bool draft)
    {
        long chartNumber = ParseChartNumber(id);
        string subformName = draft ? "受診症状サブフォーム" : "受診カルテサブ";
        object? sub = null, form = null, records = null;
        try
        {
            sub = Step(subformName + "の取得", () => AccessDispatch.Get(controls, "Item", subformName));
            form = Step(subformName + ".Formの取得", () => AccessDispatch.Get(sub, "Form"));
            records = Step(subformName + ".RecordsetCloneの取得", () => AccessDispatch.Get(form, "RecordsetClone"));
            return ReadNoteRecords(records, chartNumber, draft);
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
            finally { Release(form); Release(sub); }
        }
    }
    internal static long ParseChartNumber(string id)
    {
        if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new InvalidOperationException("カルテ番号を確認できません。");
        return number;
    }
    internal static AccessNotes ReadNoteRecords(object records, long chartNumber, bool draft = false)
    {
        var notes = new List<AccessNote>();
        var instructions = new List<AccessNote>();
        bool limited = false;
        object? fields = null;
        try
        {
            if (Convert.ToBoolean(AccessDispatch.Get(records, "BOF")) && Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
                return new("表示対象の診療記録はありません。");
            fields = AccessDispatch.Get(records, "Fields");
            // GetRows batches cross-process COM calls. Never move the live form.
            var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int fieldCount = Convert.ToInt32(AccessDispatch.Get(fields, "Count"));
            for (int i = 0; i < fieldCount; i++)
            {
                object? field = null;
                try
                {
                    field = AccessDispatch.Get(fields, "Item", i);
                    indices[Convert.ToString(AccessDispatch.Get(field, "Name")) ?? ""] = i;
                }
                finally { Release(field); }
            }
            foreach (var name in (draft ? new[] { "受診コード", "カルテ番号", "順番", "症状" } : new[] { "受診日", "受診コード", "カルテ番号", "順番", "症状", "指示欄" }))
                if (!indices.ContainsKey(name)) throw new InvalidOperationException($"診療記録に{name}がありません。");
            // The supplied query is ascending; retain its last 5,000 rows if large.
            AccessDispatch.Call(records, "MoveLast");
            int total = Convert.ToInt32(AccessDispatch.Get(records, "RecordCount"));
            limited = total > 5000;
            int remaining = Math.Min(total, 5000);
            if (limited) AccessDispatch.Call(records, "Move", -4999);
            else AccessDispatch.Call(records, "MoveFirst");
            while (remaining > 0 && !Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
            {
                int requested = Math.Min(256, remaining);
                if (AccessDispatch.Call(records, "GetRows", requested) is not Array batch || batch.Rank != 2)
                    throw new InvalidOperationException("診療記録の行データを確認できません。");
                int length = batch.GetLength(1);
                if (length < requested && !Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
                    throw new InvalidOperationException("診療記録が取得途中で変更されました。再取得します。");
                for (int row = batch.GetLowerBound(1); row <= batch.GetUpperBound(1); row++)
                {
                    object? Read(string name)
                    {
                        var value = batch.GetValue(indices[name] + batch.GetLowerBound(0), row);
                        return value is DBNull ? null : value;
                    }
                    long rowNumber = ParseChartNumber(Convert.ToString(Read("カルテ番号")) ?? "");
                    if (draft && rowNumber != chartNumber) continue;
                    if (rowNumber / 10 != chartNumber / 10)
                        throw new InvalidOperationException("診療記録の患者が表示中の患者と一致しません。");
                    string body = Convert.ToString(Read("症状")) ?? "";
                    string instruction = draft ? "" : Convert.ToString(Read("指示欄")) ?? "";
                    if (!string.IsNullOrWhiteSpace(body) || !string.IsNullOrWhiteSpace(instruction))
                    {
                        var date = draft ? null : Read("受診日");
                        var order = Read("順番");
                        var entry = new AccessNote(date == null ? null : Convert.ToDateTime(date),
                            Convert.ToString(Read("受診コード")) ?? "", rowNumber,
                            order == null ? decimal.MinValue : Convert.ToDecimal(order), body.ReplaceLineEndings("\r\n"));
                        if (!string.IsNullOrWhiteSpace(body)) notes.Add(entry);
                        if (!string.IsNullOrWhiteSpace(instruction)) instructions.Add(entry with { Text = instruction.ReplaceLineEndings("\r\n") });
                    }
                }
                remaining -= length;
            }
        }
        finally { Release(fields); }
        return new(limited ? "表示範囲の末尾5,000行から取得（上限に達しました）。" :
            draft ? (notes.Count == 0 ? "当日所見はありません。" : $"{notes.Count}件の当日所見") :
            notes.Count == 0 && instructions.Count == 0 ? "表示範囲に症状・指示の記載はありません。" : $"{notes.Count}件の症状 / {instructions.Count}件の投薬・処置（保存済み）",
            FormatNotes(notes, showDate: !draft, showIdentity: !draft), FormatNotes(instructions), notes, instructions);
    }
    internal static string FormatNotes(IEnumerable<AccessNote> notes, bool showDate = true, bool showIdentity = true)
    {
        var result = new StringBuilder();
        foreach (var day in notes.GroupBy(n => n.Date?.Date).OrderByDescending(g => g.Key))
        {
            if (result.Length > 0) result.Append("\r\n");
            if (showDate) result.Append(day.Key?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "日付なし").Append("\r\n");
            foreach (var visit in day.GroupBy(n => (n.Visit, n.ChartNumber)))
            {
                if (showIdentity) result.Append($"受診 {visit.Key.Visit} · カルテ番号 {visit.Key.ChartNumber / 10}-{visit.Key.ChartNumber % 10}\r\n");
                foreach (var note in visit.OrderBy(n => n.Order)) result.Append(note.Text).Append("\r\n");
                result.Append("\r\n");
            }
        }
        return result.ToString().TrimEnd('\r', '\n');
    }
}
