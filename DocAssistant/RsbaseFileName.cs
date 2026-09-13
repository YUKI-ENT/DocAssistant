using System.Globalization;
using System.IO;

namespace DocAssistant;

internal static class RsbaseFileName
{
    internal static string PatientId(string patientText)
    {
        var value = patientText.Split('\n').FirstOrDefault(line => line.StartsWith("カルテ番号：", StringComparison.Ordinal))?
            .Split('：', 2)[1].Trim();
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? (id / 10).ToString(CultureInfo.InvariantCulture) : "";
    }
    internal static string Build(string id, string sequence, DateTime date, string title)
    {
        id = id.Trim(); sequence = sequence.Trim(); title = title.Trim();
        static bool Digits(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
        if (!Digits(id)) throw new ArgumentException("IDには枝番を除いた数字を入力してください。");
        if (!Digits(sequence)) throw new ArgumentException("連番には数字を入力してください。");
        if (title.Length == 0 || title.Contains('~') || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("登録名を入力してください。~ やファイル名に使えない記号は使用できません。");
        var name = $"{id}~{sequence}~{date.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture)}~{title}~RSB.pdf";
        if (name.Length > 255) throw new ArgumentException("ファイル名が長すぎます。登録名などを短くしてください。");
        return name;
    }
}
