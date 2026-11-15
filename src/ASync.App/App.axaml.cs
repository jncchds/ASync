using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ASync.App.Services;

namespace ASync.App;

/// <summary>
/// The shared application: the theme (Fluent with the ASync palette, the family's component styles) and the views of
/// every view model. Heads derive from it and add their lifetime (the desktop head: window, tray, single instance).
/// </summary>
public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Light, dark, or following the system (Avalonia's Default variant).</summary>
    public static void ApplyTheme(ThemeChoice theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
