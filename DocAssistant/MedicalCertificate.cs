using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocAssistant;

public sealed class CertificateClinic
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Physician { get; set; } = "";
}

internal sealed class MedicalCertificate
{
    public int Version { get; set; } = 2;
    public string DocumentId { get; set; } = Guid.NewGuid().ToString("N");
    public int Revision { get; set; }
    public string EnvironmentId { get; set; } = "";
    public string PatientKey { get; set; } = "";
    public string ChartNumber { get; set; } = "";
    public string PatientSource { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sex { get; set; } = "";
    public DateTime? Birthday { get; set; }
    public DateTime IssueDate { get; set; } = DateTime.Today;
    public string Material { get; set; } = "";
    public string[] Sections { get; set; } = new string[4];
    public CertificateClinic Clinic { get; set; } = new();
    internal static readonly string[] Headings = ["Diagnosis", "Comorbidities", "Clinical Summary", "Treatment and Medications"];
    internal static readonly string[] Labels = ["主診断", "合併症・併存疾患", "病状・経過", "治療・処方薬"];
    internal static readonly string[] SexOptions = ["未登録", "Male", "Female", "Other", "Unknown"];
    internal int? Age => AgeOn(Birthday, IssueDate);
    internal static int? AgeOn(DateTime? birthday, DateTime date)
    {
        if (birthday == null || birthday.Value.Date > date.Date) return null;
        int years = date.Year - birthday.Value.Year;
        return date.Date < birthday.Value.Date.AddYears(years) ? years - 1 : years;
    }
    internal static string ReadSex(string text) => Read(text, "性別") switch { "男" => "Male", "女" => "Female", _ => "" };

    internal static MedicalCertificate Parse(string json)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<MedicalCertificate>(json) ?? throw new System.IO.InvalidDataException("診断書データが空です。");
        if (data.Version is not (1 or 2) || data.Sections == null || data.Clinic == null || data.IssueDate == default ||
            data.Sections.Length != (data.Version == 1 ? 7 : 4) || !Guid.TryParse(data.DocumentId, out _) || data.Revision < 0 ||
            (data.Sex != "" && !SexOptions.Skip(1).Contains(data.Sex)))
            throw new System.IO.InvalidDataException("対応していない診断書形式です。");
        if (data.Version == 1) data.Sections = data.Sections.Take(4).ToArray();
        data.Version = 2;
        if (string.IsNullOrWhiteSpace(data.ChartNumber) && data.PatientKey.LastIndexOf('|') is int split && split >= 0)
            data.ChartNumber = data.PatientKey[(split + 1)..];
        return data;
    }

    internal string RsbasePatientId()
    {
        // Older drafts stored the exact chart number only in their patient key.
        string chart = string.IsNullOrWhiteSpace(ChartNumber) && PatientKey.LastIndexOf('|') is int split && split >= 0
            ? PatientKey[(split + 1)..] : ChartNumber;
        return RsbaseFileName.PatientId("カルテ番号：" + chart);
    }

    internal static string Read(string text, string field) => text.Split('\n').Select(l => l.Trim().Split('：', 2))
        .FirstOrDefault(p => p.Length == 2 && p[0] == field)?[1].Trim() ?? "";

    internal static DateTime? ReadBirthday(string text)
    {
        var value = Read(text, "生年月日");
        var matches = Regex.Matches(value, @"(?<y>\d{4})年(?<m>\d{1,2})月(?<d>\d{1,2})日");
        if (matches.Count == 0) return null;
        var m = matches[^1];
        return DateTime.TryParseExact($"{m.Groups["y"]}-{m.Groups["m"]}-{m.Groups["d"]}", "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    internal static string CertificateNameCandidate(string input)
    {
        // Access stores surname then given name. Only reorder an unambiguous pair;
        // do not guess a boundary in an unseparated name or a multi-part name.
        var parts = Regex.Split(input.Normalize(NormalizationForm.FormKC).Trim(), @"\s+");
        if (parts.Length != 2) return "";
        var family = Romanize(parts[0]); var given = Romanize(parts[1]);
        return family == "" || given == "" ? "" : given + " " + family;
    }

    // A deterministic candidate, never an assertion about the passport spelling.
    // Preserve long vowels as OU/OO; ambiguous passport spellings require manual review.
    internal static string Romanize(string input)
    {
        var kana = new string(input.Normalize(NormalizationForm.FormKC).Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());
        var map = new Dictionary<string, string>();
        string[] rows = ["あいうえお|a i u e o", "かきくけこ|ka ki ku ke ko", "さしすせそ|sa shi su se so", "たちつてと|ta chi tsu te to", "なにぬねの|na ni nu ne no", "はひふへほ|ha hi fu he ho", "まみむめも|ma mi mu me mo", "やゆよ|ya yu yo", "らりるれろ|ra ri ru re ro", "わゐゑを|wa i e o", "がぎぐげご|ga gi gu ge go", "ざじずぜぞ|za ji zu ze zo", "だぢづでど|da ji zu de do", "ばびぶべぼ|ba bi bu be bo", "ぱぴぷぺぽ|pa pi pu pe po", "ぁぃぅぇぉゔ|a i u e o vu"];
        foreach (var row in rows)
        {
            var parts = row.Split('|'); var sounds = parts[1].Split(' ');
            for (int n = 0; n < parts[0].Length; n++) map[parts[0][n].ToString()] = sounds[n];
        }
        foreach (var pair in new[] { "き:ky", "し:sh", "ち:ch", "に:ny", "ひ:hy", "み:my", "り:ry", "ぎ:gy", "じ:j", "び:by", "ぴ:py" })
        {
            var p = pair.Split(':');
            for (int n = 0; n < 3; n++) map[p[0] + "ゃゅょ"[n]] = p[1] + "auo"[n];
        }
        var result = new StringBuilder(); bool doubleNext = false;
        for (int i = 0; i < kana.Length; i++)
        {
            char c = kana[i];
            if (c == 'っ') { if (doubleNext) return ""; doubleNext = true; continue; }
            if (c == 'ん')
            {
                if (doubleNext) return "";
                char next = i + 1 < kana.Length ? kana[i + 1] : '\0';
                result.Append("ばびぶべぼぱぴぷぺぽまみむめも".Contains(next) ? "m" : "n");
                if ("あいうえおやゆよ".Contains(next)) result.Append('\'');
                continue;
            }
            if (char.IsWhiteSpace(c)) { if (doubleNext) return ""; result.Append(' '); continue; }
            // A prolonged sound mark or unknown kana cannot safely determine a name.
            if (!map.TryGetValue(c.ToString(), out var sound)) return "";
            if (i + 1 < kana.Length && map.TryGetValue(kana.Substring(i, 2), out var combined)) { sound = combined; i++; }
            if (doubleNext) { if ("aeiou".Contains(sound[0])) return ""; result.Append(sound.StartsWith("ch") ? 't' : sound[0]); doubleNext = false; }
            result.Append(sound);
        }
        return doubleNext ? "" : Regex.Replace(result.ToString().Trim(), @"\s+", " ").ToUpperInvariant();
    }
}
