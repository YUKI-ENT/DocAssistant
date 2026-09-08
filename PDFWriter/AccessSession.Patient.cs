namespace PDFWriter;

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
            return ReadPatientControls(controls);
        }
        finally { Release(controls); Release(form); Release(forms); }
    });

    internal static string ReadPatientControls(object controls)
    {
        string Read(string name)
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
        var id = Read("カルテ番号");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("カルテ番号が空です。Accessで患者を表示してから取得してください。");
        var values = PatientFields.Select(name => $"{name}：{Read(name)}").ToArray();
        if (Read("カルテ番号") != id)
            throw new InvalidOperationException("取得中に患者が切り替わりました。もう一度取得してください。");
        return string.Join(Environment.NewLine, values);
    }
}
