using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASync.App.Services;
using ASync.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ASync.App.Desktop;

/// <summary>
/// Drives the real app for <c>scripts/smoke-desktop.sh</c>: only when <c>ASYNC_APP_AUTOMATION</c> names a socket path
/// (in a private directory the test made). One command per line, one JSON answer per line. It acts through the controls
/// a person uses: typing into named text boxes, clicking buttons (their commands), the tray menu's items; the folder
/// picker's answer is the only thing it replaces. Never enabled in normal use.
/// <list type="bullet">
/// <item><c>state</c>: page, tray, message, dialog, folders.</item>
/// <item><c>type &lt;Name&gt; &lt;text&gt;</c>, <c>click &lt;Name&gt;</c>: a control by its Name (buttons, radio and check boxes).</item>
/// <item><c>folder &lt;folder name&gt; &lt;button text&gt;</c>: a button in that folder's card.</item>
/// <item><c>pick-folder &lt;path&gt;</c>: what the next folder picker returns.</item>
/// <item><c>tray &lt;item header&gt;</c>: a tray menu item.</item>
/// <item><c>screenshot &lt;file.png&gt;</c>, <c>tray-icon &lt;status&gt; &lt;file.png&gt;</c>, <c>resize &lt;w&gt; &lt;h&gt;</c>,
///   <c>theme light|dark|system</c>, <c>show</c>, <c>close</c>, <c>quit</c>.</item>
/// </list>
/// </summary>
internal static class Automation
{
    public const string EnvironmentVariable = "ASYNC_APP_AUTOMATION";

    public static void StartIfRequested(ShellViewModel shell, MainWindow window, DesktopPlatform platform, Action quit)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is not { Length: > 0 } path || OperatingSystem.IsWindows()) return;
        if (File.Exists(path)) File.Delete(path);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        socket.Listen(4);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await socket.AcceptAsync();
                await using var stream = new NetworkStream(client);
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                while (await reader.ReadLineAsync() is { } line)
                {
                    string answer;
                    try
                    {
                        answer = await Dispatcher.UIThread.InvokeAsync(() => RunAsync(line, shell, window, platform, quit));
                    }
                    catch (Exception e)
                    {
                        answer = new JsonObject { ["ok"] = false, ["error"] = $"{e.GetType().Name}: {e.Message}" }.ToJsonString();
                    }
                    await writer.WriteLineAsync(answer);
                }
            }
        });
    }

    private static JsonObject Ok(JsonNode? value = null) => new() { ["ok"] = true, ["value"] = value };

    private static async Task<string> RunAsync(string line, ShellViewModel shell, MainWindow window, DesktopPlatform platform, Action quit)
    {
        var parts = line.Split(' ', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        JsonObject result = parts[0] switch
        {
            "state" => Ok(State(shell, window)),
            "type" => Type(window, arg),
            "click" => Click(window, arg),
            "folder" => ClickInFolder(window, arg),
            "pick-folder" => PickFolder(platform, arg),
            "tray" => Tray(arg),
            "screenshot" => Save(window, arg),
            "tray-icon" => SaveTrayIcon(arg),
            "resize" => Resize(window, arg),
            "theme" => Theme(platform, arg),
            "show" => Do(() => DesktopApp.ShowWindow(window)),
            "close" => Do(window.Close),
            "quit" => Do(() => Dispatcher.UIThread.Post(quit)),
            _ => new JsonObject { ["ok"] = false, ["error"] = $"unknown command {parts[0]}" },
        };
        await Task.Yield();
        return result.ToJsonString();
    }

    private static JsonObject Do(Action action)
    {
        action();
        return Ok();
    }

    private static JsonObject State(ShellViewModel shell, MainWindow window) => new()
    {
        ["page"] = shell.Page.GetType().Name,
        ["settings"] = shell.SettingsPage is not null,
        ["dialog"] = shell.Dialog?.GetType().Name,
        ["tray"] = shell.TrayStatus.ToString(),
        ["trayToolTip"] = shell.TrayToolTip,
        ["message"] = shell.Message,
        ["messageIsError"] = shell.MessageIsError,
        ["windowVisible"] = window.IsVisible,
        ["agentState"] = shell.Status?.State.ToString(),
        ["device"] = shell.Status?.Device?.DeviceName,
        ["enrollError"] = shell.Enroll.Error,
        ["userCode"] = shell.Enroll.Pending?.UserCode,
        ["folders"] = new JsonArray([.. shell.Dashboard.Folders.Select(f => (JsonNode)new JsonObject
        {
            ["id"] = f.Id, ["name"] = f.Name, ["state"] = f.State.Text, ["localPath"] = f.HasLocalPath ? f.LocalPath : null,
            ["paused"] = f.Paused, ["locked"] = f.Locked, ["busy"] = f.IsBusy, ["progress"] = f.ProgressText,
            ["lastResult"] = f.LastResultText, ["lastOk"] = f.LastResultOk, ["next"] = f.NextRunText, ["alert"] = f.AlertText,
        })]),
    };

    private static IEnumerable<Control> All(Visual root) => root.GetVisualDescendants().OfType<Control>();

    private static Control Find(MainWindow window, string name) =>
        All(window).FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)
        ?? throw new InvalidOperationException($"no visible control named {name}");

    private static JsonObject Type(MainWindow window, string arg)
    {
        var parts = arg.Split(' ', 2);
        if (Find(window, parts[0]) is not TextBox box) throw new InvalidOperationException($"{parts[0]} is not a text box");
        box.Text = parts.Length > 1 ? parts[1] : "";
        return Ok();
    }

    private static JsonObject Click(MainWindow window, string name) => Press(Find(window, name));

    private static JsonObject Press(Control control)
    {
        switch (control)
        {
            case Button { Command: { } command } button:
                if (!button.IsEffectivelyEnabled || !command.CanExecute(button.CommandParameter))
                    throw new InvalidOperationException($"{button.Name ?? button.Content?.ToString()} is disabled");
                command.Execute(button.CommandParameter);
                return Ok();
            case RadioButton radio:
                radio.IsChecked = true;
                return Ok();
            case ToggleButton toggle:
                toggle.IsChecked = toggle.IsChecked != true;
                return Ok();
            default:
                throw new InvalidOperationException($"{control.Name} cannot be clicked");
        }
    }

    private static JsonObject ClickInFolder(MainWindow window, string arg)
    {
        var at = arg.LastIndexOf(" | ", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("usage: folder <folder name> | <button text>");
        var (folder, text) = (arg[..at], arg[(at + 3)..]);
        var button = All(window).OfType<Button>().FirstOrDefault(b =>
                         b.DataContext is FolderViewModel f && f.Name == folder && b.Content as string == text && b.IsEffectivelyVisible)
                     ?? throw new InvalidOperationException($"no button \"{text}\" for the folder {folder}");
        return Press(button);
    }

    private static JsonObject PickFolder(DesktopPlatform platform, string path)
    {
        platform.PickFolderOverride = () =>
        {
            platform.PickFolderOverride = null;
            return path;
        };
        return Ok();
    }

    private static JsonObject Tray(string header)
    {
        var app = Application.Current as DesktopApp ?? throw new InvalidOperationException("not the desktop app");
        var item = app.TrayItems().FirstOrDefault(i => i.Header == header) ?? throw new InvalidOperationException($"no tray item {header}");
        (item.Command ?? throw new InvalidOperationException($"tray item {header} has no command")).Execute(item.CommandParameter);
        return Ok(new JsonObject { ["headers"] = new JsonArray([.. app.TrayItems().Select(i => (JsonNode?)i.Header)]) });
    }

    private static JsonObject Save(MainWindow window, string file)
    {
        var size = window.ClientSize;
        var scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(file, PngBitmapEncoderOptions.Default);
        return Ok(file);
    }

    /// <summary><c>tray-icon &lt;ok|syncing|problem&gt; &lt;file&gt;</c>: one of the tray's icons, 64 px.</summary>
    private static JsonObject SaveTrayIcon(string arg)
    {
        var parts = arg.Split(' ', 2);
        var status = Enum.Parse<TrayStatus>(parts[0], ignoreCase: true);
        using var bitmap = TrayIcons.Render(status, 64);
        bitmap.Save(parts[1], PngBitmapEncoderOptions.Default);
        return Ok(parts[1]);
    }

    private static JsonObject Resize(MainWindow window, string arg)
    {
        var wh = arg.Split(' ');
        window.Width = double.Parse(wh[0], System.Globalization.CultureInfo.InvariantCulture);
        window.Height = double.Parse(wh[1], System.Globalization.CultureInfo.InvariantCulture);
        return Ok();
    }

    private static JsonObject Theme(DesktopPlatform platform, string arg)
    {
        platform.ApplyTheme(Enum.Parse<ThemeChoice>(arg, ignoreCase: true));
        return Ok();
    }

    // Keeps JsonSerializer referenced for trimming analysis of JsonNode serialisation.
    internal static string Serialize(JsonNode node) => node.ToJsonString(JsonSerializerOptions.Default);
}
