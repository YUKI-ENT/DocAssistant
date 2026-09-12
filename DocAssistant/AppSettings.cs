using System.IO;
using System.Text.Json;

namespace DocAssistant;

public sealed record TemplateItem(string Name, string Path)
{
    public override string ToString() => Name;
}
public sealed class AppSettings
{
    public string SaveFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string TemplateFolder { get; set; } = FindWorkspace();
    public List<string> TemplateFiles { get; set; } = [];
    public string DefaultTemplate { get; set; } = "";
    public string AccessDatabasePath { get; set; } = "";
    public string AccessFormName { get; set; } = "患者マスター";
    public LlmSettings Llm { get; set; } = new();
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocAssistant.sln"))) return directory.FullName;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocAssistant", "settings.json");
    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public List<TemplateItem> GetTemplates()
    {
        var paths = new List<string>(TemplateFiles ?? []);
        if (Directory.Exists(TemplateFolder)) paths.AddRange(Directory.EnumerateFiles(TemplateFolder, "*.pdf"));
        return paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new TemplateItem(Path.GetFileNameWithoutExtension(path), path)).ToList();
    }
}
