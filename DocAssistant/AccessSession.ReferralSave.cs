using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    internal Task<ReferralLetter> SaveReferralAsync(ReferralPatient patient, ReferralLetter baseline, ReferralLetter draft) => RunAsync(() =>
    {
        object? app = null;
        try
        {
            app = GetRunningAccess() ?? throw new InvalidOperationException("Accessで患者を表示してください。");
            return SaveReferralRecord(app, patient, baseline, draft, () =>
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (timedOut) throw new InvalidOperationException("Accessの応答待ちがタイムアウトしたため、保存を中止しました。");
            });
        }
        finally { Release(app); }
    });

    internal static ReferralLetter SaveReferralRecord(object app, ReferralPatient patient, ReferralLetter baseline, ReferralLetter draft, Action? verifySession = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return SaveReferralAttempt(app, patient, baseline, draft, verifySession); }
            catch (ReferralNumberConflictException) when (attempt < 2) { }
        }
    }

    private sealed class ReferralNumberConflictException : InvalidOperationException
    {
        internal ReferralNumberConflictException(Exception inner) : base("紹介番号が他の登録と重複しました。下書きは保持しています。時間をおいて保存してください。", inner) { }
    }

    private static ReferralLetter SaveReferralAttempt(object app, ReferralPatient patient, ReferralLetter baseline, ReferralLetter draft, Action? verifySession)
    {
        if (draft.ChartNumber / 10 != patient.ChartNumber / 10 || draft.ChartNumber != baseline.ChartNumber || draft.Number != baseline.Number)
            throw new InvalidOperationException("紹介状の対象患者・紹介番号が一致しません。");
        if (draft.Number == null && draft.ChartNumber != patient.ChartNumber)
            throw new InvalidOperationException("新規紹介状の枝番が表示中患者と一致しません。");
        if (draft.Date == null) throw new InvalidOperationException("紹介状の日付を入力してください。");
        VerifyReferralPatient(app, patient);
        EnsureReferralFormClosed(app);
        object? database = null, records = null, fields = null;
        bool editing = false;
        try
        {
            database = AccessDispatch.Call(app, "CurrentDb");
            var where = draft.Number is long id
                ? $"[紹介番号] = {id.ToString(CultureInfo.InvariantCulture)} AND [カルテ番号] = {draft.ChartNumber.ToString(CultureInfo.InvariantCulture)}"
                : $"[カルテ番号] = {draft.ChartNumber.ToString(CultureInfo.InvariantCulture)}";
            records = AccessDispatch.Call(database, "OpenRecordset", "SELECT * FROM [紹介状] WHERE " + where + ";", 2);
            fields = AccessDispatch.Get(records, "Fields");
            if (draft.Number == null)
            {
                object? numberField = null;
                try
                {
                    numberField = AccessDispatch.Get(fields, "Item", "紹介番号");
                    if (Convert.ToInt32(AccessDispatch.Get(numberField, "Type")) != 4 ||
                        (Convert.ToInt32(AccessDispatch.Get(numberField, "Attributes")) & 16) != 0)
                        throw new InvalidOperationException("紹介番号には長整数型（オートナンバーではない）が必要です。");
                }
                finally { Release(numberField); }
                EnsureUniqueReferralNumber(database);
            }
            else if (Convert.ToBoolean(AccessDispatch.Get(records, "EOF")))
                throw new InvalidOperationException("紹介状が削除・変更されています。再取得してください。");

            var values = draft.Values();
            var previous = baseline.Values();
            // Validate every changed value before beginning a record edit.
            var changes = new Dictionary<string, object?>();
            foreach (var (name, value) in values)
            {
                if (draft.Number != null && Equals(value, previous[name])) continue;
                object? field = null;
                try
                {
                    field = AccessDispatch.Get(fields, "Item", name);
                    int type = Convert.ToInt32(AccessDispatch.Get(field, "Type"));
                    if ((name == "日付" && type != 8) || (name != "日付" && type is not (10 or 12)))
                        throw new InvalidOperationException($"{name}のフィールド型が想定と異なります。テーブル定義を確認してください。");
                    if (value is string text && type == 10 && text.Length > Convert.ToInt32(AccessDispatch.Get(field, "Size")))
                        throw new InvalidOperationException($"{name}が保存可能な文字数を超えています。");
                    changes[name] = value is "" ? DBNull.Value : value;
                }
                finally { Release(field); }
            }
            int? newNumber = draft.Number == null ? ReadNextReferralNumber(database) : null;
            verifySession?.Invoke();
            AccessDispatch.Set(records, "LockEdits", true);
            AccessDispatch.Call(records, draft.Number == null ? "AddNew" : "Edit"); editing = true;
            if (draft.Number != null && ReadCurrentReferral(fields) != baseline)
                throw new InvalidOperationException("取得後に紹介状が変更されています。上書きせず中止しました。再取得して内容を確認してください。");
            if (draft.Number == null)
            {
                changes["カルテ番号"] = draft.ChartNumber;
                changes["紹介番号"] = newNumber;
            }
            foreach (var (name, value) in changes)
            {
                object? field = null;
                try { field = AccessDispatch.Get(fields, "Item", name); AccessDispatch.Set(field, "Value", value); }
                finally { Release(field); }
            }
            VerifyReferralPatient(app, patient);
            EnsureReferralFormClosed(app);
            verifySession?.Invoke();
            try { AccessDispatch.Call(records, "Update"); }
            catch (COMException ex) when (draft.Number == null && IsDuplicateReferralNumber(app, ex))
            { throw new ReferralNumberConflictException(ex); }
            editing = false;
            AccessDispatch.Set(records, "Bookmark", AccessDispatch.Get(records, "LastModified"));
            var saved = ReadCurrentReferral(fields);
            if (saved.Number != (newNumber ?? draft.Number) || saved.ChartNumber != draft.ChartNumber)
                throw new IOException("保存後の紹介状を確認できません。");
            return saved;
        }
        finally
        {
            try
            {
                if (records != null)
                {
                    try { if (editing) AccessDispatch.Call(records, "CancelUpdate"); }
                    finally { AccessDispatch.Call(records, "Close"); }
                }
            }
            finally { Release(fields); Release(records); Release(database); }
        }
    }

    internal static int NextReferralNumber(object? maximum)
    {
        if (maximum is null or DBNull) return 1;
        var value = Convert.ToInt64(maximum, CultureInfo.InvariantCulture);
        if (value < 0 || value >= int.MaxValue)
            throw new InvalidOperationException("紹介番号が長整数型の採番可能範囲を超えています。");
        return checked((int)value + 1);
    }

    private static int ReadNextReferralNumber(object database)
    {
        object? records = null;
        try
        {
            // SHOUKAIJOU uses the last global number + 1. MAX explicitly avoids
            // dependence on query ordering and includes rows outside patient joins.
            records = AccessDispatch.Call(database, "OpenRecordset", "SELECT Max([紹介番号]) AS [最大紹介番号] FROM [紹介状];", 4, 4);
            var rows = ReadReferralRows(records);
            if (rows.Count != 1) throw new InvalidOperationException("紹介番号の最大値を取得できません。");
            return NextReferralNumber(rows[0]["最大紹介番号"]);
        }
        finally
        {
            try { if (records != null) AccessDispatch.Call(records, "Close"); }
            finally { Release(records); }
        }
    }

    private static void EnsureUniqueReferralNumber(object database)
    {
        object? tables = null, table = null, indexes = null;
        try
        {
            tables = AccessDispatch.Get(database, "TableDefs"); table = AccessDispatch.Get(tables, "Item", "紹介状");
            indexes = AccessDispatch.Get(table, "Indexes");
            for (int i = 0; i < Convert.ToInt32(AccessDispatch.Get(indexes, "Count")); i++)
            {
                object? index = null, fields = null, field = null;
                try
                {
                    index = AccessDispatch.Get(indexes, "Item", i);
                    if (!Convert.ToBoolean(AccessDispatch.Get(index, "Unique"))) continue;
                    fields = AccessDispatch.Get(index, "Fields");
                    if (Convert.ToInt32(AccessDispatch.Get(fields, "Count")) != 1) continue;
                    field = AccessDispatch.Get(fields, "Item", 0);
                    if (Convert.ToString(AccessDispatch.Get(field, "Name")) == "紹介番号") return;
                }
                finally { Release(field); Release(fields); Release(index); }
            }
            throw new InvalidOperationException("紹介番号の重複を防ぐ一意インデックスを確認できません。テーブル定義を確認してください。");
        }
        finally { Release(indexes); Release(table); Release(tables); }
    }

    private static bool IsDuplicateReferralNumber(object app, COMException exception)
    {
        // Retry only an explicit DAO duplicate-key failure, never an uncertain write.
        if (exception.ErrorCode == unchecked((int)0x800A0BCE)) return true;
        if (((uint)exception.ErrorCode & 0xFFFF0000) != 0x800A0000) return false;
        object? engine = null, errors = null, error = null;
        try
        {
            engine = AccessDispatch.Get(app, "DBEngine"); errors = AccessDispatch.Get(engine, "Errors");
            int count = Convert.ToInt32(AccessDispatch.Get(errors, "Count"));
            if (count == 0) return false;
            error = AccessDispatch.Get(errors, "Item", count - 1);
            return Convert.ToInt32(AccessDispatch.Get(error, "Number")) == 3022;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        finally { Release(error); Release(errors); Release(engine); }
    }

    internal static ReferralLetter ReadCurrentReferral(object fields)
    {
        var row = new Dictionary<string, object?>();
        foreach (var name in new[] { "紹介番号", "カルテ番号", "日付", "紹介先1", "紹介先2", "紹介先先生", "傷病名", "紹介目的", "治療", "検査", "備考", "検査結果" })
        {
            object? field = null;
            try
            {
                field = AccessDispatch.Get(fields, "Item", name);
                var value = AccessDispatch.Get(field, "Value"); row[name] = value is DBNull ? null : value;
            }
            finally { Release(field); }
        }
        return ParseReferralRows([row], Convert.ToInt64(row["カルテ番号"])).Single();
    }

    private static void EnsureReferralFormClosed(object app)
    {
        if (IsReferralFormOpen(app))
            throw new InvalidOperationException("Accessの紹介状フォームを閉じてから保存してください。フォーム側の採番・中断処理との干渉を避けます。");
    }
}
