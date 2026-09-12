using System.Globalization;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    internal static readonly string[] PatientFields = ["氏名", "フリガナ", "性別コード１", "カルテ番号", "郵便番号", "住所１", "住所２", "TEL"];

    public Task<string> ReadPatientAsync() => RunAsync(() =>
    {
        if (application == null) throw new InvalidOperationException("先にAccessに接続してください。");
        Step("患者情報取得前のMDB照合", () => { VerifyDatabase(application, databasePath); return true; });
        object? forms = null, form = null, controls = null;
        try
        {
            forms = Step("Formsの取得", () => AccessDispatch.Get(application, "Forms"));
            form = Step("患者マスターの取得", () => AccessDispatch.Get(forms, "Item", "患者マスター"));
            controls = Step("患者マスターのControls取得", () => AccessDispatch.Get(form, "Controls"));
            return ReadPatientControls(controls, form);
        }
        finally { Release(controls); Release(form); Release(forms); }
    });

    internal static string ReadPatientControls(object controls, object? form = null)
    {
        string Read(string name) => ReadControlValue(controls, name);
        var id = Read("カルテ番号");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("カルテ番号が空です。Accessで患者を表示してから取得してください。");
        var values = PatientFields.Select(name => name == "性別コード１"
            ? $"性別：{FormatPatientSex(Read(name))}" : $"{name}：{Read(name)}").ToList();
        if (form != null) values.Insert(3, $"生年月日：{ReadPatientBirthday(form)}");
        if (Read("カルテ番号") != id)
            throw new InvalidOperationException("取得中に患者が切り替わりました。もう一度取得してください。");
        return string.Join(Environment.NewLine, values);
    }
    internal static string FormatPatientSex(string value) => value.Trim() switch
    {
        "1" => "男", "2" => "女", "" => "未登録", _ => $"不明（コード：{value}）"
    };
    internal static string FormatPatientBirthday(string era, string year, string month, string day)
    {
        var parts = new[] { era, year, month, day }.Select(s => s.Trim()).ToArray();
        if (parts.All(string.IsNullOrEmpty)) return "未登録";
        var display = $"{(parts[0] == "" ? "年号不明" : parts[0])}{(parts[1] == "" ? "不明" : parts[1])}年{(parts[2] == "" ? "不明" : parts[2])}月{(parts[3] == "" ? "不明" : parts[3])}日";
        var culture = new CultureInfo("ja-JP");
        var calendar = new JapaneseCalendar();
        culture.DateTimeFormat.Calendar = calendar;
        int eraNumber = calendar.Eras.FirstOrDefault(e => culture.DateTimeFormat.GetEraName(e) == parts[0]);
        string yearNumber = parts[1] == "元" ? "1" : parts[1];
        if (eraNumber == 0 || !int.TryParse(yearNumber, out int y) ||
            !int.TryParse(parts[2], out int m) || !int.TryParse(parts[3], out int d)) return display;
        try
        {
            var date = calendar.ToDateTime(y, m, d, 0, 0, 0, 0, eraNumber);
            // JapaneseCalendar can accept dates beyond an era's boundaries.
            if (calendar.GetEra(date) != eraNumber || calendar.GetYear(date) != y) return display;
            return display + "（" + date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture) + "）";
        }
        catch (ArgumentOutOfRangeException) { return display; }
    }
    internal static string ReadPatientBirthday(object form)
    {
        object? record = null, fields = null;
        try
        {
            // Read the current form record without navigating or closing its Recordset.
            record = AccessDispatch.Get(form, "Recordset");
            if (Convert.ToBoolean(AccessDispatch.Get(record, "BOF")) || Convert.ToBoolean(AccessDispatch.Get(record, "EOF"))) return "未登録";
            fields = AccessDispatch.Get(record, "Fields");
            string Read(string name)
            {
                object? field = null;
                try
                {
                    field = AccessDispatch.Get(fields, "Item", name);
                    var value = AccessDispatch.Get(field, "Value");
                    return value is null or DBNull ? "" : Convert.ToString(value) ?? "";
                }
                finally { Release(field); }
            }
            return FormatPatientBirthday(Read("年号"), Read("生年"), Read("月"), Read("日"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or MissingMemberException)
        {
            return "取得できません";
        }
        finally { Release(fields); Release(record); }
    }
    internal static string ReadControlValue(object controls, string name)
    {
        object? control = null;
        try
        {
            control = Step($"患者情報：{name}の取得", () => AccessDispatch.Get(controls, "Item", name));
            var value = Step($"患者情報：{name}.Valueの取得", () => AccessDispatch.Get(control, "Value"));
            return value is null or DBNull ? "" : Convert.ToString(value) ?? "";
        }
        finally { Release(control); }
    }

}
