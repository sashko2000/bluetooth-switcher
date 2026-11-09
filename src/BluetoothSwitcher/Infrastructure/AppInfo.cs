using System.Reflection;

namespace BluetoothSwitcher.Infrastructure;

internal static class AppInfo
{
    /// <summary>Version without the source revision, e.g. "1.1.0".</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
