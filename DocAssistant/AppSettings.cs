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
    public List<TemplateItem> Templates { get; set; } = [];
    public bool RsbasePdfNaming { get; set; }
    public string DefaultTemplate { get; set; } = "";
    public string AccessDatabasePath { get; set; } = "";
    public string AccessFormName { get; set; } = "患者マスター";
    public LlmSettings Llm { get; set; } = new();
    public CertificateClinic CertificateClinic { get; set; } = new();
    public bool CertificateRsbaseNaming { get; set; } = true;
    public string CertificateRegistrationTitle { get; set; } = "英文診断書";
    public string CertificateDatabasePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocAssistant", "Data", "certificates.sqlite");
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocAssistant.sln"))) return directory.FullName;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocAssistant", "settings.json");
    public static AppSettings Load()
        => Load(SettingsPath);
    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
    }
    public void Save()
        => Save(SettingsPath);
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public List<TemplateItem> GetTemplates()
    {
        var items = GetRegisteredTemplates();
        if (Directory.Exists(TemplateFolder)) items.AddRange(Directory.EnumerateFiles(TemplateFolder, "*.pdf")
            .Select(path => new TemplateItem(Path.GetFileNameWithoutExtension(path), path)));
        return items.Where(t => File.Exists(t.Path)).Select(t => t with { Path = Path.GetFullPath(t.Path) })
            .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public List<TemplateItem> GetRegisteredTemplates() => (Templates ?? []).Concat((TemplateFiles ?? [])
        .Select(path => new TemplateItem(Path.GetFileNameWithoutExtension(path), path)))
        .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
}
