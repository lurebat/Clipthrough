using System.Reflection;

namespace Clipthrough.Presentation;

public static class AppIdentity
{
    private static readonly Assembly s_assembly = typeof(AppIdentity).Assembly;

    public static string ProductName => "Clipthrough";

    public static string DisplayVersion
    {
        get
        {
            var informational = s_assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational.Split('+')[0];
            }

            return s_assembly.GetName().Version?.ToString(3) ?? "dev";
        }
    }
}
