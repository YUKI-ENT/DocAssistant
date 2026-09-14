using System.Globalization;

namespace DocAssistant;

internal sealed record ReferralPatient(string DatabasePath, long ChartNumber, string Name)
{
    internal string Display => $"{Name}　カルテ番号 {ChartNumber / 10}-{ChartNumber % 10}";
    internal bool SameIdentity(ReferralPatient? other) => other != null && ChartNumber == other.ChartNumber &&
        string.Equals(DatabasePath, other.DatabasePath, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ReferralLetter(long? Number, long ChartNumber, DateTime? Date,
    string Destination1 = "", string Destination2 = "", string Doctor = "", string Diagnosis = "",
    string Purpose = "", string Treatment = "", string Tests = "", string Remarks = "", string TestResults = "")
{
    internal ReferralLetter CopyFor(long patient, DateTime today) => this with { Number = null, ChartNumber = patient, Date = today.Date };
    internal Dictionary<string, object?> Values() => new()
    {
        ["日付"] = Date, ["紹介先1"] = Destination1, ["紹介先2"] = Destination2, ["紹介先先生"] = Doctor,
        ["傷病名"] = Diagnosis, ["紹介目的"] = Purpose, ["治療"] = Treatment, ["検査"] = Tests,
        ["備考"] = Remarks, ["検査結果"] = TestResults
    };
}

internal sealed record ReferralSuggestion(string Value, long Count)
{
    public override string ToString() => Value;
}
internal sealed record ReferralChoices(IReadOnlyList<ReferralSuggestion> Destinations1,
    IReadOnlyList<ReferralSuggestion> Destinations2, IReadOnlyList<ReferralSuggestion> Doctors);
internal sealed record ReferralHistory(IReadOnlyList<ReferralLetter> Letters, ReferralChoices? Choices, string ChoicesStatus,
    IReadOnlyList<string>? Purposes = null, IReadOnlyList<string>? Templates = null, MedicationHistory? Medication = null, IReadOnlyList<string>? Diagnoses = null);

internal sealed record ReferralPrescription(string DateLabel, string Content)
{
    public string Label
    {
        get
        {
            var preview = string.Join(" / ", Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return DateLabel + " — " + (preview.Length > 70 ? preview[..70] + "…" : preview);
        }
    }
    internal string Text => $"処方日：{DateLabel}\r\n{Content}";
    internal static IReadOnlyList<ReferralPrescription> FromHistory(MedicationHistory? history) =>
        (history?.Days ?? []).Where(day => day.Rows?.Count > 0)
            .OrderByDescending(day => day.Rows!.Max(row => row.Date))
            .Select(day => new ReferralPrescription(day.Key,
                string.Join("\r\n", day.Rows!.GroupBy(row => (row.Visit, row.Number))
                    .SelectMany(group => group.OrderBy(row => row.Order))
                    .Select(row => $"{row.Name}　{row.Quantity}{row.Unit}")
                    .SelectMany(text => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    .Where(line => !line.Contains("処方箋", StringComparison.Ordinal) && !line.Contains("一般名", StringComparison.Ordinal)))))
            .Where(prescription => !string.IsNullOrWhiteSpace(prescription.Content)).ToArray();
}
