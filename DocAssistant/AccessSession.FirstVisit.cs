using System.Globalization;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    private static DateTime ReadFirstVisit(object app, string id) => ReadVisitBoundary(app, id, false);
    private static DateTime ReadLastVisit(object app, string id) => ReadVisitBoundary(app, id, true);

    internal static string VisitBoundarySql(string id, bool latest)
    {
        long number = ParseChartNumber(id);
        long lower = number - number % 10;
        string from = lower.ToString(CultureInfo.InvariantCulture);
        string to = ((decimal)lower + 10).ToString(CultureInfo.InvariantCulture);
        return $"SELECT {(latest ? "Max" : "Min")}([受診日]) AS [対象日] FROM [受診] WHERE [カルテ番号] >= {from} AND [カルテ番号] < {to};";
    }

    internal static DateTime ReadVisitBoundary(object app, string id, bool latest)
    {
        string label = latest ? "最終受診日" : "初診日";
        object? database = null, records = null, fields = null, field = null;
        try
        {
            database = AccessDispatch.Call(app, "CurrentDb");
            records = AccessDispatch.Call(database, "OpenRecordset",
                VisitBoundarySql(id, latest), 4, 4);
            fields = AccessDispatch.Get(records, "Fields");
            field = AccessDispatch.Get(fields, "Item", "対象日");
            var value = AccessDispatch.Get(field, "Value");
            if (value is null or DBNull) throw new InvalidOperationException($"{label}を確認できません。日付指定を使用してください。");
            return Convert.ToDateTime(value).Date;
        }
        finally
        {
            Release(field); Release(fields);
            try { if (records != null) AccessDispatch.Call(records, "Close"); }
            finally { Release(records); Release(database); }
        }
    }
}
