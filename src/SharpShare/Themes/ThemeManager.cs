using Avalonia;
using Avalonia.Styling;

namespace SharpShare.Themes;

/// <summary>
/// Manages theme switching between light and dark modes at runtime. Uses Avalonia's built-in ThemeVariant system with
/// compile-time ThemeDictionaries.
/// </summary>
public static class ThemeManager
{
    public static void ApplyTheme(bool isDarkMode)
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        app.RequestedThemeVariant = isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
