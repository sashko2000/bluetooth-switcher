using System.Globalization;

namespace BluetoothSwitcher.Localization;

/// <summary>UI language selection. An empty code means "follow the Windows display language".</summary>
internal static class AppLanguage
{
    /// <summary>Languages with a Strings.*.resx file. English is the neutral (fallback) language.</summary>
    public static IReadOnlyList<(string Code, string NativeName)> Supported { get; } =
    [
        ("en", "English"),
        ("ru", "Русский"),
        ("uk", "Українська"),
    ];

    // Captured before the app changes anything, so "System default" can be restored later.
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    public static void Apply(string? code)
    {
        CultureInfo culture;
        try
        {
            culture = string.IsNullOrWhiteSpace(code) ? SystemUiCulture : CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            culture = SystemUiCulture;
        }

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
