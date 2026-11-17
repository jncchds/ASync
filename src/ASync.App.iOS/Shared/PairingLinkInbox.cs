using System.ComponentModel;
using ASync.App.ViewModels;

namespace ASync.App.iOS;

/// <summary>
/// The <c>async://pair?s=…&amp;c=…</c> links iOS hands the app (opened from another app, the Camera app's QR reader, or the
/// link that launched it): kept until the shell shows the enrollment form (a cold start first connects to the agent), then
/// put into it; a link arriving when the device is enrolled already is dropped (of no use then). Other URLs are ignored.
/// Plain C#, linked into ASync.App.Tests.
/// </summary>
public sealed class PairingLinkInbox
{
    private readonly Lock _gate = new();
    private string? _pending;
    private ShellViewModel? _shell;

    /// <summary>Keeps a pairing link for the shell; false for any other URL.</summary>
    public bool Offer(string link)
    {
        if (!link.StartsWith("async:", StringComparison.OrdinalIgnoreCase)) return false;
        lock (_gate) _pending = link;
        return true;
    }

    public bool HasPending
    {
        get { lock (_gate) return _pending is not null; }
    }

    /// <summary>Delivers to this shell from now on, whenever its page changes (on the UI thread, where the shell raises it).</summary>
    public void Attach(ShellViewModel shell)
    {
        if (_shell == shell) return;
        _shell = shell;
        shell.PropertyChanged += OnShellChanged;
        if (HasPending) Deliver(shell); // the form may be shown already
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page) && HasPending) Deliver(_shell);
    }

    /// <summary>
    /// Fills the enrollment form with the pending link (on the UI thread). True when it was used. The link stays while there
    /// is no shell or it is still connecting to the agent; it is dropped when the device is enrolled already.
    /// </summary>
    public bool Deliver(ShellViewModel? shell)
    {
        if (shell is null) return false;
        string link;
        lock (_gate)
        {
            if (_pending is null) return false;
            if (shell.Page is not (EnrollViewModel or DashboardViewModel)) return false; // not decided yet: wait for the page
            link = _pending;
            _pending = null;
        }
        return shell.Page is EnrollViewModel && shell.Enroll.ApplyLink(link);
    }
}
