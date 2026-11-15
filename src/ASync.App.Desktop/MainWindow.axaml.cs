using Avalonia.Controls;

namespace ASync.App.Desktop;

/// <summary>
/// The window. Closing it only hides it while the tray icon is there (the app keeps running in the tray); Quit in the
/// tray menu ends the app. Neither stops the agent service.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Set by Quit: the next close really closes.</summary>
    public bool Quitting { get; set; }

    /// <summary>Whether closing hides to the tray (false where no tray host exists, so the app can still be ended).</summary>
    public bool HideOnClose { get; set; } = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Quitting && HideOnClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
