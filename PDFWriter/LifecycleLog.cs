using System.IO;

namespace PDFWriter;

// Lifecycle markers only: never write PDF names, entered text or drawing data.
internal static class LifecycleLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFWriter", "Logs", $"lifecycle-{Environment.ProcessId}.log");

    internal static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
