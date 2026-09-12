using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;

namespace DocAssistant;

public partial class MainWindow
{
    private MedicationHistory? shownMedication;
    private string medicationPatientKey = "";
    private readonly Dictionary<string, Expander> medicationExpanders = new();

    private void ShowMedicationHistory(MedicationHistory? history)
    {
        if (ReferenceEquals(shownMedication, history)) return;
        shownMedication = history;
        string key = history?.PatientKey ?? "";
        if (key != medicationPatientKey)
        {
            medicationPatientKey = key;
            medicationExpanders.Clear();
            MedicationDays.Children.Clear();
            MedicationScroll.ScrollToTop();
        }
        var days = history?.Days ?? [];
        var keys = days.Select(d => d.Key).ToHashSet();
        foreach (var removed in medicationExpanders.Keys.Where(k => !keys.Contains(k)).ToArray())
        {
            MedicationDays.Children.Remove(medicationExpanders[removed]);
            medicationExpanders.Remove(removed);
        }
        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            if (!medicationExpanders.TryGetValue(day.Key, out var expander))
            {
                expander = new Expander
                {
                    Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10),
                    Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 227, 235)),
                    BorderThickness = new Thickness(1), Foreground = new SolidColorBrush(Color.FromRgb(51, 70, 92)),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                expander.Expanded += (_, _) =>
                {
                    if (expander.Content != null) return;
                    var value = (MedicationDay)expander.Tag;
                    expander.Content = VisitContent(value);
                };
                medicationExpanders[day.Key] = expander;
            }
            if (expander.Tag is MedicationDay previous && previous != day && expander.Content != null)
                expander.Content = VisitContent(day);
            expander.Tag = day;
            expander.Header = new TextBlock { Text = day.Header, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 2, 0, 2) };
            if (expander.Content is TextBox box && box.Text != day.Text) box.Text = day.Text;
            // Reuse controls so refresh preserves expanded days, scroll and text selection.
            int current = MedicationDays.Children.IndexOf(expander);
            if (current != i)
            {
                if (current >= 0) MedicationDays.Children.Remove(expander);
                MedicationDays.Children.Insert(i, expander);
            }
        }
    }
    private static TextBox ChartValue(string text) => new()
    {
        Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4),
        Background = Brushes.Transparent, FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static RichTextBox ClinicalValue(string text)
    {
        var box = new RichTextBox { IsReadOnly = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0, 4, 0, 4), FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        EnableChartDrag(box);
        SetClinicalText(box, text);
        return box;
    }

    private static void SetClinicalText(RichTextBox box, string text)
    {
        if (box.Tag is string previous && previous == text) return;
        box.Tag = text;
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var run = new Run(line);
            // The chart marker is an underscore; a preceding slash/yen sign is optional.
            if (line.Contains('_') || line.Contains('＿'))
            {
                run.Foreground = Brushes.Red;
                run.TextDecorations = TextDecorations.Underline;
            }
            var paragraph = new Paragraph(run) { Margin = new Thickness(0) };
            if (line.Contains('$')) paragraph.Background = Brushes.Yellow;
            document.Blocks.Add(paragraph);
        }
        box.Document = document;
    }

    private static StackPanel VisitContent(MedicationDay day)
    {
        var panel = new StackPanel { Margin = new Thickness(3, 12, 3, 0) };
        foreach (var (title, text) in new[] { ("所見", day.Notes), ("処置・指示", day.Instructions) })
        {
            var content = new StackPanel();
            var accent = new SolidColorBrush(title == "所見" ? Color.FromRgb(92, 133, 148) : Color.FromRgb(153, 119, 126));
            content.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = accent, Margin = new Thickness(0, 0, 0, 4) });
            content.Children.Add(ClinicalValue(string.IsNullOrWhiteSpace(text) ? "記載なし（取得できた範囲）" : text));
            panel.Children.Add(new Border { Child = content, Background = Brushes.Transparent,
                BorderBrush = accent, BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 0, 0, 14) });
        }
        return panel;
    }

}
