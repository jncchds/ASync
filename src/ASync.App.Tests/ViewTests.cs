using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using ASync.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(ASync.App.Tests.TestApp))]

namespace ASync.App.Tests;

/// <summary>The shared <see cref="App"/> (theme, styles, view templates) on the headless platform, rendered with Skia.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>
/// The views in Avalonia.Headless: real layout, bindings and input on the shared views with a fake agent. Covers what the
/// view models alone cannot: that the controls are wired to the commands, the secret is masked, and the layout fits a
/// phone-sized window.
/// </summary>
public class ViewTests
{
    private static async Task<(Window Window, ShellFixture Fixture)> ShowAsync(AgentStatusDto status, double width = 760, double height = 720)
    {
        var f = new ShellFixture().Start(status);
        var window = new Window { Width = width, Height = height, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null);
        return (window, f);
    }

    /// <summary>Runs the dispatcher until the condition holds (the shell's loop continues on the UI thread).</summary>
    private static async Task Pump(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    private static IEnumerable<T> All<T>(Window window) => window.GetVisualDescendants().OfType<T>();

    [AvaloniaFact]
    public async Task Enroll_TypingACodeAndClicking_EnrollsThroughTheAgent()
    {
        var (window, f) = await ShowAsync(Statuses.NotEnrolled());
        await using var _ = f;
        await Pump(() => All<EnrollView>(window).Any());

        Named<TextBox>(window, "Server").Text = "https://sync.example.com";
        var code = Named<TextBox>(window, "Code");
        code.Focus();
        window.KeyTextInput("K7QD-M2XP-9RWA");
        Assert.Equal("K7QD-M2XP-9RWA", f.Shell.Enroll.Code);

        var button = Named<Button>(window, "Enroll");
        Assert.True(button.IsEffectivelyEnabled);
        button.Command!.Execute(null);
        await Pump(() => f.Agent.Calls.Count > 0);
        Assert.Equal("enroll https://sync.example.com K7QD-M2XP-9RWA test-host Linux", f.Agent.Calls[0]);
    }

    [AvaloniaFact]
    public async Task Dashboard_ShowsEveryFolderCard_WithItsButtons()
    {
        var (window, f) = await ShowAsync(Statuses.Connected(
            Statuses.Folder() with { LastResult = new FolderRunDto(1, "Docs", true, "1 up", false, DateTimeOffset.UtcNow) },
            Statuses.Folder(2, "Photos", "") with { Mode = SyncMode.Backup },
            Statuses.Folder(3, "Vault") with { Encryption = FolderEncryption.EndToEnd, Locked = true }));
        await using var _ = f;
        await Pump(() => All<DashboardView>(window).Any());

        Assert.Equal("laptop", Named<TextBlock>(window, "DeviceName").Text);
        Assert.Equal("Connected", Named<TextBlock>(window, "ConnectionState").Text);
        var texts = All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Docs", texts);
        Assert.Contains("Up to date", texts);
        Assert.Contains("No local folder", texts);
        Assert.Contains("🔒 End-to-end", texts);
        var buttons = All<Button>(window).Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.Contains("Choose folder…", buttons); // Photos has no path
        Assert.Contains("Unlock…", buttons);        // Vault is locked
        Assert.Equal(2, buttons.Count(b => b == "Open folder"));
    }

    [AvaloniaFact]
    public async Task Unlock_TheSecretIsMasked()
    {
        var (window, f) = await ShowAsync(Statuses.Connected(Statuses.Folder() with { Encryption = FolderEncryption.EndToEnd, Locked = true }));
        await using var _ = f;
        f.Shell.Dashboard.Folders[0].UnlockCommand.Execute(null);
        await Pump(() => All<UnlockView>(window).Any(v => v.IsEffectivelyVisible));

        var secret = Named<TextBox>(window, "Secret");
        Assert.Equal('•', secret.PasswordChar);
        Assert.False(secret.RevealPassword);
        secret.Focus();
        window.KeyTextInput("hunter2-hunter2");
        Assert.Equal("hunter2-hunter2", f.Shell.Dialog!.Secret);
        // Nothing on screen shows it.
        Assert.DoesNotContain(All<TextBlock>(window), t => t.Text?.Contains("hunter2") == true);
    }

    [AvaloniaFact]
    public async Task ASmallWindow_FitsWithoutHorizontalOverflow()
    {
        var (window, f) = await ShowAsync(Statuses.Connected(
            Statuses.Folder(1, "A folder with a rather long name for a phone", "/home/alice/a/very/long/path/that/must/wrap/somewhere/on/a/phone") with
            {
                LastResult = new FolderRunDto(1, "x", false, "a long failure message that has to wrap rather than widen the card", false, DateTimeOffset.UtcNow),
            }), width: 360, height: 700);
        await using var _ = f;
        await Pump(() => All<DashboardView>(window).Any());
        window.UpdateLayout();

        var outside = All<Control>(window)
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.TranslatePoint(new Point(c.Bounds.Width, 0), window) is { } right && right.X > 360.5)
            .Select(c => $"{c.GetType().Name} {c.Name} {c.Bounds.Width}")
            .ToList();
        Assert.Empty(outside);
    }

    [AvaloniaFact]
    public async Task TheThemes_UseThePalette()
    {
        var (window, f) = await ShowAsync(Statuses.Connected());
        await using var _ = f;
        static string Bg(Window w) => w.TryFindResource("Bg", w.ActualThemeVariant, out var b) && b is Avalonia.Media.ISolidColorBrush brush ? brush.Color.ToString() : "none";

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal("#ff130c17", Bg(window));
        using (var frame = window.CaptureRenderedFrame()) Assert.NotNull(frame);
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Equal("#fffaf5fb", Bg(window));
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaFact]
    public async Task Settings_ThemeRadioButtons_SwitchTheTheme()
    {
        var (window, f) = await ShowAsync(Statuses.Connected());
        await using var _ = f;
        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => All<SettingsView>(window).Any(v => v.IsEffectivelyVisible));

        Named<RadioButton>(window, "ThemeDark").IsChecked = true;
        Assert.Equal(ThemeChoice.Dark, f.Platform.Themes[^1]);
        Named<RadioButton>(window, "ThemeLight").IsChecked = true;
        Assert.Equal(ThemeChoice.Light, f.Platform.Themes[^1]);
        Assert.Equal(ThemeChoice.Light, f.Settings.Settings.Theme);
    }
}
