using System.Globalization;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    private ReferralChoices? cachedReferralChoices;
    private string referralChoicesDatabase = "";
    private DateTime referralChoicesRead;

    internal static string ReferralSql(long number)
    {
        if (number < 0) throw new ArgumentOutOfRangeException(nameof(number));
        var lower = number - number % 10;
        return "SELECT [紹介番号], [カルテ番号], [日付], [紹介先1], [紹介先2], [紹介先先生], " +
            "[傷病名], [紹介目的], [治療], [検査], [備考], [検査結果] FROM [紹介状] " +
            $"WHERE [カルテ番号] >= {lower.ToString(CultureInfo.InvariantCulture)} " +
            $"AND [カルテ番号] < {((decimal)lower + 10).ToString(CultureInfo.InvariantCulture)} ORDER BY [紹介番号] DESC;";
    }

    internal static string ReferralChoicesSql(string field)
    {
        if (field is not ("紹介先1" or "紹介先2" or "紹介先先生")) throw new ArgumentOutOfRangeException(nameof(field));
        return $"SELECT Trim([{field}]) AS [候補], Count(*) AS [使用回数] FROM [紹介状] " +
            $"WHERE [{field}] Is Not Null AND Trim([{field}]) <> '' " +
            $"GROUP BY Trim([{field}]) ORDER BY Count(*) DESC, Trim([{field}]);";
    }

    internal const string ReferralPurposesSql = "SELECT [紹介目的] FROM [紹介目的リスト] WHERE [紹介目的] Is Not Null;";
    internal const string ReferralTemplatesSql = "SELECT [コメントコード], [コメント区分コード], [コメント] FROM [紹介状コメントリスト] WHERE [コメント区分コード] = 13 ORDER BY [コメントコード];";

    internal Task<ReferralHistory> ReadReferralsAsync(ReferralPatient patient, bool refreshChoices) => RunAsync(() =>
    {
        object? app = null;
        try
        {
            app = GetRunningAccess() ?? throw new InvalidOperationException("Accessで患者を表示してください。");
            VerifyReferralPatient(app, patient);
            var letters = ParseReferralRows(ReadReferralQuery(app, ReferralSql(patient.ChartNumber)), patient.ChartNumber);
            string choicesStatus = "全患者の過去の紹介状から使用頻度順に表示します。";
            try
            {
                if (refreshChoices || cachedReferralChoices == null ||
                    !SameDatabase(referralChoicesDatabase, patient.DatabasePath) || DateTime.UtcNow - referralChoicesRead > TimeSpan.FromMinutes(5))
                {
                    IReadOnlyList<ReferralSuggestion> Read(string field) => ReadReferralQuery(app, ReferralChoicesSql(field))
                        .Select(row => new ReferralSuggestion(Convert.ToString(row["候補"]) ?? "", Convert.ToInt64(row["使用回数"])))
                        .Where(s => !string.IsNullOrWhiteSpace(s.Value)).ToArray();
                    cachedReferralChoices = new(Read("紹介先1"), Read("紹介先2"), Read("紹介先先生"));
                    referralChoicesDatabase = patient.DatabasePath; referralChoicesRead = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cachedReferralChoices = null;
                choicesStatus = "紹介先候補を取得できませんでした。直接入力できます。";
            }
            IReadOnlyList<string> ReadList(string sql, string field, string label)
            {
                try
                {
                    return ReadReferralQuery(app, sql).Select(row => (Convert.ToString(row[field]) ?? "").ReplaceLineEndings("\r\n"))
                        .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    choicesStatus += $" {label}を取得できませんでした。";
                    return [];
                }
            }
            var purposes = ReadList(ReferralPurposesSql, "紹介目的", "紹介目的リスト");
            var templates = ReadList(ReferralTemplatesSql, "コメント", "紹介状コメントリスト");
            MedicationHistory? medication = null;
            try { medication = ReadMedicationHistory(app, patient.ChartNumber.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                choicesStatus += " 投薬履歴を取得できませんでした。再取得してください。";
            }
            VerifyReferralPatient(app, patient);
            return new ReferralHistory(letters, cachedReferralChoices, choicesStatus, purposes, templates, medication);
        }
        finally { Release(app); }
    });

    internal static void VerifyReferralPatient(object app, ReferralPatient patient)
    {
        VerifyDatabase(app, patient.DatabasePath);
        object? forms = null, form = null, controls = null;
        try
        {
            forms = AccessDispatch.Get(app, "Forms"); form = AccessDispatch.Get(forms, "Item", "患者マスター");
            if (Convert.ToBoolean(AccessDispatch.Get(form, "NewRecord"))) throw new InvalidOperationException("Accessで患者を選択してください。");
            controls = AccessDispatch.Get(form, "Controls");
            if (ParseChartNumber(ReadControlValue(controls, "カルテ番号")) != patient.ChartNumber)
                throw new InvalidOperationException("Accessの患者が変わりました。紹介状の対象患者を確認して再取得してください。");
        }
        finally { Release(controls); Release(form); Release(forms); }
    }

    private static List<Dictionary<string, object?>> ReadReferralQuery(object app, string sql)
    {
        object? database = null, records = null;
        try
        {
            database = AccessDispatch.Call(app, "CurrentDb");
            // Snapshot/read-only DAO queries through the running Access instance.
            records = Step("紹介状の読み取り", () => AccessDispatch.Call(database, "OpenRecordset", sql, 4, 4));
            return ReadReferralRows(records);
        }
        finally
        {
            try { if (records != null) AccessDispatch.Call(records, "Close"); }
            finally { Release(records); Release(database); }
        }
    }

    internal static List<Dictionary<string, object?>> ReadReferralRows(object records)
    {
        var result = new List<Dictionary<string, object?>>();
        if (Convert.ToBoolean(AccessDispatch.Get(records, "EOF"))) return result;
        object? fields = null;
        try
        {
            fields = AccessDispatch.Get(records, "Fields");
            var names = new List<string>();
            for (int i = 0; i < Convert.ToInt32(AccessDispatch.Get(fields, "Count")); i++)
            {
                object? field = null;
                try { field = AccessDispatch.Get(fields, "Item", i); names.Add(Convert.ToString(AccessDispatch.Get(field, "Name")) ?? ""); }
                finally { Release(field); }
            }
            while (!Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
            {
                if (AccessDispatch.Call(records, "GetRows", 256) is not Array batch || batch.Rank != 2 || batch.GetLength(0) != names.Count || batch.GetLength(1) == 0)
                    throw new InvalidOperationException("紹介状の読み取り形式を確認できません。");
                for (int r = batch.GetLowerBound(1); r <= batch.GetUpperBound(1); r++)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int f = 0; f < names.Count; f++)
                    {
                        var value = batch.GetValue(f + batch.GetLowerBound(0), r);
                        row[names[f]] = value is DBNull ? null : value;
                    }
                    result.Add(row);
                }
            }
            return result;
        }
        finally { Release(fields); }
    }

    internal static IReadOnlyList<ReferralLetter> ParseReferralRows(IEnumerable<Dictionary<string, object?>> rows, long patient)
    {
        var letters = new List<ReferralLetter>();
        foreach (var row in rows)
        {
            string Text(string field) => (Convert.ToString(row[field]) ?? "").ReplaceLineEndings("\r\n");
            var number = ParseChartNumber(Text("カルテ番号"));
            if (number / 10 != patient / 10) throw new InvalidOperationException("紹介状の患者番号が一致しません。");
            var id = ParseChartNumber(Text("紹介番号"));
            DateTime? date = row["日付"] == null ? null : Convert.ToDateTime(row["日付"], CultureInfo.CurrentCulture);
            letters.Add(new(id, number, date, Text("紹介先1"), Text("紹介先2"), Text("紹介先先生"), Text("傷病名"),
                Text("紹介目的"), Text("治療"), Text("検査"), Text("備考"), Text("検査結果")));
        }
        if (letters.Select(l => l.Number).Distinct().Count() != letters.Count) throw new InvalidOperationException("紹介番号が重複しています。");
        return letters.OrderByDescending(l => l.Number).ToArray();
    }
}
