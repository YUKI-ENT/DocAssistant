using System.Globalization;

namespace PDFWriter;

internal sealed partial class AccessSession
{
    private static DateTime ReadFirstVisit(object app, string id)
    {
        long number = ParseChartNumber(id);
        long lower = number - number % 10;
        string from = lower.ToString(CultureInfo.InvariantCulture);
        string to = ((decimal)lower + 10).ToString(CultureInfo.InvariantCulture);
        object? database = null, records = null, fields = null, field = null;
        try
        {
            database = AccessDispatch.Call(app, "CurrentDb");
            records = AccessDispatch.Call(database, "OpenRecordset",
                $"SELECT Min([受診日]) AS [初診日] FROM [受診] WHERE [カルテ番号] >= {from} AND [カルテ番号] < {to};", 4, 4);
            fields = AccessDispatch.Get(records, "Fields");
            field = AccessDispatch.Get(fields, "Item", "初診日");
            var value = AccessDispatch.Get(field, "Value");
            if (value is null or DBNull) throw new InvalidOperationException("初診日を確認できません。日付指定を使用してください。");
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
