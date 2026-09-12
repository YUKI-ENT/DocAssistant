using System.Globalization;

namespace PDFWriter;

internal sealed record CurrentMedication(string Text = "", string Status = "", string Visit = "");

internal sealed partial class AccessSession
{
    internal static CurrentMedication ReadCurrentMedication(object controls, string id, string subform = "受診投薬サブフォーム", string nameField = "薬名", bool hasOrder = true)
    {
        object? sub = null, form = null, subControls = null, records = null;
        try
        {
            sub = AccessDispatch.Get(controls, "Item", subform);
            form = AccessDispatch.Get(sub, "Form");
            subControls = AccessDispatch.Get(form, "Controls");
            string visit = ReadControlValue(subControls, "受診コード");
            if (string.IsNullOrWhiteSpace(visit)) return new("", "現在の受診コードがありません。");
            records = AccessDispatch.Get(form, "RecordsetClone");
            var result = ReadCurrentMedicationRecords(records, ParseChartNumber(id), visit, nameField, hasOrder, subform == "受診投薬サブフォーム");
            if (ReadControlValue(subControls, "受診コード") != visit)
                throw new InvalidOperationException("取得中に受診が切り替わりました。");
            return result with { Visit = visit };
        }
        finally
        {
            try
            {
                if (records != null)
                {
                    try { AccessDispatch.Call(records, "Close"); }
                    finally { Release(records); }
                }
            }
            finally { Release(subControls); Release(form); Release(sub); }
        }
    }

    internal static CurrentMedication ReadCurrentMedicationRecords(object records, long patient, string visit, string nameField = "薬名", bool hasOrder = true, bool showUnit = false)
    {
        if (Convert.ToBoolean(AccessDispatch.Get(records, "BOF")) && Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
            return new("", "データなし");
        object? fields = null;
        var rows = new List<(decimal Order, string Text)>();
        try
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
            foreach (var name in (hasOrder ? new[] { "受診コード", "カルテ番号", "順番", nameField, "数量" } : new[] { "受診コード", "カルテ番号", nameField, "数量" }))
                if (!indices.ContainsKey(name)) throw new InvalidOperationException($"当日診療に{name}がありません。");
            AccessDispatch.Call(records, "MoveFirst");
            while (!Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
            {
                if (AccessDispatch.Call(records, "GetRows", 256) is not Array batch || batch.Rank != 2)
                    throw new InvalidOperationException("当日診療の形式を確認できません。");
                if (batch.GetLength(1) < 256 && !Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
                    throw new InvalidOperationException("取得中に診療データが変更されました。");
                for (int row = batch.GetLowerBound(1); row <= batch.GetUpperBound(1); row++)
                {
                    object? Read(string name)
                    {
                        var value = batch.GetValue(indices[name] + batch.GetLowerBound(0), row);
                        return value is DBNull ? null : value;
                    }
                    if (Convert.ToString(Read("受診コード")) != visit) continue;
                    if (ParseChartNumber(Convert.ToString(Read("カルテ番号")) ?? "") != patient)
                        throw new InvalidOperationException("当日診療の患者番号が一致しません。");
                    string name = Convert.ToString(Read(nameField)) ?? "";
                    string quantity = Convert.ToString(Read("数量"), CultureInfo.InvariantCulture) ?? "";
                    string unit = showUnit && indices.ContainsKey("区分") ? (Convert.ToString(Read("区分")) ?? "").Trim() : "";
                    var order = hasOrder ? Read("順番") : null;
                    rows.Add((order == null ? decimal.MinValue : Convert.ToDecimal(order),
                        $"{(string.IsNullOrWhiteSpace(name) ? "名称未登録" : name)}　{(quantity == "" ? "未登録" : quantity)}{unit}"));
                }
            }
        }
        finally { Release(fields); }
        return new(string.Join("\r\n", rows.OrderBy(r => r.Order).Select(r => r.Text)).ReplaceLineEndings("\r\n"),
            rows.Count == 0 ? "データなし" : "");
    }
}
