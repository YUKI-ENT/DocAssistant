using System.Globalization;
using System.Runtime.InteropServices;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    internal sealed record LiveDraftRow(long Patient, string Visit, decimal Order, string Text);

    // Reading controls does not commit the form's edit buffer or move its record.
    // A continuous form exposes the controls for its current row; the clone supplies all other rows.
    internal static LiveDraftRow? ReadLiveDraftRow(object form)
    {
        object? controls = null;
        try
        {
            controls = AccessDispatch.Get(form, "Controls");
            string? Read(string field, bool text = false)
            {
                object? control = null;
                try
                {
                    control = FindDraftControl(controls, field);
                    if (control == null) return null;
                    if (text)
                    {
                        try { return Convert.ToString(AccessDispatch.Get(control, "Text")) ?? ""; }
                        // Access Text is available only while the text box has focus (2185).
                        catch (COMException ex) when ((ex.HResult & 0xFFFF) == 2185) { }
                        catch (MissingMemberException) { }
                    }
                    var value = AccessDispatch.Get(control, "Value");
                    return value is null or DBNull ? "" : Convert.ToString(value) ?? "";
                }
                finally { Release(control); }
            }
            var patient = Read("カルテ番号");
            var visit = Read("受診コード");
            var order = Read("順番");
            var body = Read("症状", true);
            // Layouts without the identity controls cannot safely overlay a row.
            if (patient == null || visit == null || order == null || body == null) return null;
            if (string.IsNullOrWhiteSpace(body) && (string.IsNullOrWhiteSpace(patient) ||
                string.IsNullOrWhiteSpace(visit) || string.IsNullOrWhiteSpace(order))) return null;
            if (string.IsNullOrWhiteSpace(visit) || !decimal.TryParse(order, NumberStyles.Number, CultureInfo.CurrentCulture, out var sequence))
                throw new InvalidOperationException("当日所見の受診コード・順番を確認できません。");
            return new(ParseChartNumber(patient), visit, sequence, body.ReplaceLineEndings("\r\n"));
        }
        catch (MissingMemberException) { return null; }
        finally { Release(controls); }
    }

    private static object? FindDraftControl(object controls, string field)
    {
        // A control may have a different name from the field it is bound to.
        int count = Convert.ToInt32(AccessDispatch.Get(controls, "Count"));
        for (int i = 0; i < count; i++)
        {
            object? control = null;
            try
            {
                control = AccessDispatch.Get(controls, "Item", i);
                string source;
                try { source = Convert.ToString(AccessDispatch.Get(control, "ControlSource")) ?? ""; }
                catch (COMException) { continue; } // Labels/buttons have no ControlSource.
                catch (MissingMemberException) { continue; }
                if (source.Trim().Trim('[', ']') != field) continue;
                var found = control; control = null;
                return found;
            }
            finally { Release(control); }
        }
        return null;
    }

    internal static AccessNotes MergeLiveDraftRow(AccessNotes saved, LiveDraftRow? live, long patient)
    {
        if (live == null) return saved with { Status = saved.Status + "（画面の入力欄を照合できないため、レコードから取得）" };
        if (live.Patient != patient)
            throw new InvalidOperationException("当日所見の患者が表示中の患者と一致しません。");
        var rows = (saved.Notes ?? []).Where(n => n.Visit == live.Visit && n.ChartNumber == patient && n.Order != live.Order).ToList();
        if (!string.IsNullOrWhiteSpace(live.Text)) rows.Add(new(null, live.Visit, patient, live.Order, live.Text));
        return new(rows.Count == 0 ? "当日所見はありません。（画面の入力欄を確認済み）" : $"{rows.Count}件の当日所見（画面の入力内容を反映）",
            FormatNotes(rows, false, false), Notes: rows, Orders: [], Visits: [live.Visit]);
    }
}
