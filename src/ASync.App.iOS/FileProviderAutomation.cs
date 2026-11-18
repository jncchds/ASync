using System.Text;
using System.Text.Json;
using ASync.App.ViewModels;
using FileProvider;
using Foundation;

namespace ASync.App.iOS;

/// <summary>
/// Tests only: the File Provider commands of <see cref="Automation"/> (task 25, <c>scripts/smoke-ios-files.sh</c>). The app reaches
/// its own domain as any app reaches a provider: through the user-visible URL of an item (<c>getUserVisibleURLForItemIdentifier</c>)
/// and coordinated reads and writes, which make the system call the extension (enumerate, fetch, create). <c>fp-state</c> (the
/// group decision, where the store is, the move, the domains, the signals, the extension's footprint and log tail), <c>fp-url
/// [identifier]</c>, <c>fp-ls &lt;path&gt;</c> (a directory under the domain's root: names, sizes), <c>fp-read &lt;path&gt;</c> (copies the
/// file into the app's tmp/ and answers that path and its size), <c>fp-write &lt;path&gt; &lt;base64&gt;</c>, <c>fp-delete &lt;path&gt;</c>,
/// <c>fp-signal</c> (the working set), <c>fp-log</c>, <c>fp-open &lt;path&gt;</c> (the Files app at that place, for screenshots).
/// </summary>
internal static class FileProviderAutomation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The value of <c>NSFileProviderRootContainerItemIdentifier</c> (the extension checks the same spelling at start).</summary>
    private const string RootContainer = "NSFileProviderRootContainerItemIdentifier";

    public static async Task<string?> RunAsync(string op, string arg, ShellViewModel shell)
    {
        switch (op)
        {
            case "fp-state":
                return await StateAsync();
            case "fp-url":
                return (await UrlAsync(arg.Length == 0 ? RootContainer : arg)).Path ?? "";
            case "fp-ls":
                return await ListAsync(arg);
            case "fp-read":
                return await ReadAsync(arg);
            case "fp-write":
            {
                var space = arg.LastIndexOf(' ');
                return await WriteAsync(arg[..space], Convert.FromBase64String(arg[(space + 1)..]));
            }
            case "fp-delete":
                return await DeleteAsync(arg);
            case "fp-signal":
                FileProviderDomains.Changed(null);
                return "signalled";
            case "fp-log":
                return Tail(Path.Combine(FileProviderDomains.SharedDirectory ?? "", "fileprovider.log"), 40);
            case "fp-open":
            {
                var url = Combine(await RootAsync(), arg);
                var opened = await UIKit.UIApplication.SharedApplication.OpenUrlAsync(new NSUrl("shareddocuments://" + Uri.EscapeDataString(url.Path!).Replace("%2F", "/")),
                    new UIKit.UIApplicationOpenUrlOptions());
                return opened ? "opened" : "not opened";
            }
            default:
                return null;
        }
    }

    private static async Task<string> StateAsync()
    {
        var d = FileProviderDomains.Decision;
        var shared = FileProviderDomains.SharedDirectory;
        string? root = null;
        try { root = (await UrlAsync(RootContainer)).Path; }
        catch (Exception e) { root = $"error: {e.Message}"; }
        return JsonSerializer.Serialize(new
        {
            d.Shared, d.Note, d.Why, group = d.GroupContainer, store = FileProviderDomains.StoreDirectory, migration = FileProviderDomains.Migration,
            enabled = FileProviderDomains.Enabled, registered = FileProviderDomains.Registered, domains = await FileProviderDomains.DomainsAsync(),
            signals = FileProviderDomains.Signals, problem = FileProviderDomains.LastProblem, root,
            account = shared is null ? null : SharedAccount.Read(shared) is { } a ? $"{a.Username} revoked={a.Revoked is not null} folders={a.Folders.Count}" : null,
            generation = shared is null ? (long?)null : FileProviderSignals.Read(shared).Generation,
            footprint = shared is null ? null : Tail(Path.Combine(shared, "footprint.txt"), 1),
        }, Json);
    }

    private static Task<NSUrl> UrlAsync(string identifier)
    {
        if (!FileProviderDomains.Replicated) throw new PlatformNotSupportedException("The File Provider needs iOS 16.");
        var manager = FileProviderDomains.Manager() ?? throw new InvalidOperationException("No File Provider domain is registered.");
        var done = new TaskCompletionSource<NSUrl>();
        manager.GetUserVisibleUrl(new NSString(identifier), (url, error) =>
        {
            if (url is not null) done.TrySetResult(url);
            else done.TrySetException(new IOException(error?.LocalizedDescription ?? "no URL"));
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static Task<NSUrl> RootAsync() => UrlAsync(RootContainer);

    private static NSUrl Combine(NSUrl root, string path) =>
        path.Length == 0 ? root : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(root, (u, s) => u.Append(s, false));

    /// <summary>A coordinated read of a directory: the system enumerates it through the extension first.</summary>
    private static async Task<string> ListAsync(string path)
    {
        var url = Combine(await RootAsync(), path);
        return await Task.Run(() =>
        {
            using var coordinator = new NSFileCoordinator();
            string? result = null;
            coordinator.CoordinateRead(url, NSFileCoordinatorReadingOptions.WithoutChanges, out var error, actual =>
            {
                var entries = NSFileManager.DefaultManager.GetDirectoryContent(actual, NSArray.FromNSObjects(NSUrl.FileSizeKey, NSUrl.IsDirectoryKey),
                    NSDirectoryEnumerationOptions.None, out var listError);
                result = listError is not null
                    ? $"error: {listError.LocalizedDescription} ({listError.Domain} {listError.Code})"
                    : JsonSerializer.Serialize((entries ?? []).Select(e =>
                    {
                        e.TryGetResource(NSUrl.FileSizeKey, out var size);
                        e.TryGetResource(NSUrl.IsDirectoryKey, out var dir);
                        return new { name = e.LastPathComponent, size = (size as NSNumber)?.Int64Value, dir = (dir as NSNumber)?.BoolValue == true };
                    }).OrderBy(e => e.name, StringComparer.Ordinal), Json);
            });
            return error is not null ? $"error: {error.LocalizedDescription} ({error.Domain} {error.Code})" : result ?? "error: no answer";
        });
    }

    private static async Task<string> ReadAsync(string path)
    {
        var url = Combine(await RootAsync(), path);
        return await Task.Run(() =>
        {
            var target = Path.Combine(IosAgent.Home, "tmp", "fp-read-" + Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(path));
            using var coordinator = new NSFileCoordinator();
            string? result = null;
            coordinator.CoordinateRead(url, NSFileCoordinatorReadingOptions.WithoutChanges, out var error, actual =>
            {
                try
                {
                    File.Copy(actual.Path!, target, overwrite: true);
                    result = $"{target} {new FileInfo(target).Length}";
                }
                catch (Exception e) { result = $"error: {e.Message}"; }
            });
            return error is not null ? $"error: {error.LocalizedDescription} ({error.Domain} {error.Code})" : result ?? "error: no answer";
        });
    }

    private static async Task<string> WriteAsync(string path, byte[] content)
    {
        var url = Combine(await RootAsync(), path);
        return await Task.Run(() =>
        {
            using var coordinator = new NSFileCoordinator();
            string? result = null;
            coordinator.CoordinateWrite(url, NSFileCoordinatorWritingOptions.ForReplacing, out var error, actual =>
            {
                try
                {
                    File.WriteAllBytes(actual.Path!, content);
                    result = "written";
                }
                catch (Exception e) { result = $"error: {e.GetType().Name}: {e.Message}"; }
            });
            return error is not null ? $"error: {error.LocalizedDescription} ({error.Domain} {error.Code})" : result ?? "error: no answer";
        });
    }

    private static async Task<string> DeleteAsync(string path)
    {
        var url = Combine(await RootAsync(), path);
        return await Task.Run(() =>
        {
            using var coordinator = new NSFileCoordinator();
            string? result = null;
            coordinator.CoordinateWrite(url, NSFileCoordinatorWritingOptions.ForDeleting, out var error, actual =>
            {
                result = NSFileManager.DefaultManager.Remove(actual, out var removeError) ? "deleted" : $"error: {removeError?.LocalizedDescription}";
            });
            return error is not null ? $"error: {error.LocalizedDescription}" : result ?? "error: no answer";
        });
    }

    private static string Tail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path, Encoding.UTF8);
            return string.Join(" | ", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception e) { return $"error: {e.Message}"; }
    }
}
