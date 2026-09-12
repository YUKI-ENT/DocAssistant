using System.Reflection;

namespace DocAssistant;

internal static class AppVersion
{
    internal static string Display => typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
