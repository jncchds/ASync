using System.Text.Json.Nodes;
using global::Android.Content;
using global::Android.Database;
using global::Android.Provider;
using ASync.Client.Core.Documents;

namespace ASync.App.Android;

/// <summary>
/// Tests only: automation commands (<c>docs …</c>, see <see cref="Automation"/>) that drive the documents provider through
/// <see cref="ContentResolver"/> and <see cref="DocumentsContract"/>, the same calls the Files app makes, from inside the app
/// (its own uid may call its provider; any other caller needs MANAGE_DOCUMENTS). Document ids are the provider's.
/// <code>
/// docs roots | list &lt;id&gt; | stat &lt;id&gt; | open &lt;id&gt; | grant &lt;id&gt; | write &lt;id&gt; &lt;text&gt; | create &lt;parent&gt; &lt;mime&gt; &lt;name&gt;
/// docs mkdir &lt;parent&gt; &lt;name&gt; | rename &lt;id&gt; &lt;name&gt; | delete &lt;id&gt; | copy &lt;id&gt; &lt;parent&gt; | move &lt;id&gt; &lt;from&gt; &lt;to&gt;
/// docs search &lt;text&gt; | recent | thumb &lt;id&gt; | cache [max file bytes] | start &lt;command&gt; | result &lt;ticket&gt;
/// </code>
/// </summary>
internal static class DocumentsAutomation
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task<string>> Pending = new();
    private static int _nextTicket;

    private static global::Android.Net.Uri Doc(string id) => DocumentsContract.BuildDocumentUri(AndroidDocuments.Authority, id)!;

    public static async Task<string> RunAsync(Context context, string line)
    {
        var resolver = context.ContentResolver!;
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var op = parts.Length > 1 ? parts[1] : "roots";
        var rest = parts.Length > 2 ? parts[2] : "";
        string[] Args(int n) => rest.Split(' ', n, StringSplitOptions.RemoveEmptyEntries);
        switch (op)
        {
            case "start":
            {
                // docs start <docs command>: runs it in the background (a broadcast's answer has a time limit) → a ticket.
                var ticket = Interlocked.Increment(ref _nextTicket);
                Pending[ticket] = Task.Run(async () =>
                {
                    try { return await RunAsync(context, "docs " + rest); }
                    catch (Exception e) { return $"error: {e.GetType().Name}: {e.Message}"; }
                });
                return ticket.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            case "result":
                return Pending.TryGetValue(int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture), out var task)
                    ? task.IsCompleted ? task.Result : "pending"
                    : "no such ticket";
            case "roots":
                return Rows(resolver.Query(DocumentsContract.BuildRootsUri(AndroidDocuments.Authority)!, null, null, null, null)).ToJsonString();
            case "list":
            {
                // The provider answers "loading" when the server is slow; the Files app waits for the change notice, this polls.
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    using var cursor = resolver.Query(DocumentsContract.BuildChildDocumentsUri(AndroidDocuments.Authority, rest)!, null, null, null, null);
                    var extras = cursor?.Extras;
                    if (extras?.GetBoolean(DocumentsContract.ExtraLoading) == true && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    return new JsonObject
                    {
                        ["entries"] = Rows(cursor, dispose: false),
                        ["info"] = extras?.GetString(DocumentsContract.ExtraInfo),
                        ["error"] = extras?.GetString(DocumentsContract.ExtraError),
                        ["loading"] = extras?.GetBoolean(DocumentsContract.ExtraLoading) == true,
                    }.ToJsonString();
                }
            }
            case "stat":
                return Rows(resolver.Query(Doc(rest), null, null, null, null)).ToJsonString();
            case "open":
            {
                // What the provider hands out for reading, without reading it here: a cached file is a real file, a large one
                // a proxy descriptor (/mnt/appfuse/…). Reading a proxy descriptor in this process would hang it: this process
                // serves it, and a thread blocked in that FUSE read cannot take part in the runtime's stop-the-world GC. The
                // smoke test reads through another process instead (`grant`, then `content read` in the adb shell).
                var fd = resolver.OpenFileDescriptor(Doc(rest), "r")!;
                try
                {
                    return new JsonObject { ["fd"] = global::Android.Systems.Os.Readlink($"/proc/self/fd/{fd.Fd}"), ["statSize"] = fd.StatSize }.ToJsonString();
                }
                finally
                {
                    fd.Close(); // Dispose alone does not close it (the provider would see the remote side leaked)
                }
            }
            case "grant":
                // A single-document read grant for the adb shell, as DocumentsUI gives the app a person picked a file for.
                context.GrantUriPermission("com.android.shell", Doc(rest), ActivityFlags.GrantReadUriPermission);
                return "ok";
            case "write":
            {
                var a = Args(2);
                var fd = resolver.OpenFileDescriptor(Doc(a[0]), "wt")!;
                // AutoCloseOutputStream closes the descriptor itself: a descriptor only disposed is "leaked" for the
                // provider, which then saves nothing (as Android wants: the writer may have failed).
                using (var output = new global::Android.OS.ParcelFileDescriptor.AutoCloseOutputStream(fd))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(a.Length > 1 ? a[1] : "");
                    output.Write(bytes, 0, bytes.Length);
                    output.Close();
                }
                await WaitForSavesAsync();
                return "ok";
            }
            case "create":
            {
                var a = Args(3);
                return DocumentsContract.CreateDocument(resolver, Doc(a[0]), a[1], a[2]) is { } uri ? DocumentsContract.GetDocumentId(uri)! : "none";
            }
            case "mkdir":
            {
                var a = Args(2);
                return DocumentsContract.CreateDocument(resolver, Doc(a[0]), DocumentsContract.Document.MimeTypeDir, a[1]) is { } uri ? DocumentsContract.GetDocumentId(uri)! : "none";
            }
            case "rename":
            {
                var a = Args(2);
                return DocumentsContract.RenameDocument(resolver, Doc(a[0]), a[1]) is { } uri ? DocumentsContract.GetDocumentId(uri)! : a[0];
            }
            case "delete":
                return DocumentsContract.DeleteDocument(resolver, Doc(rest)) ? "ok" : "not deleted";
            case "copy":
            {
                var a = Args(2);
                return DocumentsContract.CopyDocument(resolver, Doc(a[0]), Doc(a[1])) is { } uri ? DocumentsContract.GetDocumentId(uri)! : "none";
            }
            case "move":
            {
                var a = Args(3);
                return DocumentsContract.MoveDocument(resolver, Doc(a[0]), Doc(a[1]), Doc(a[2])) is { } uri ? DocumentsContract.GetDocumentId(uri)! : "none";
            }
            case "search":
            {
                // As DocumentsUI asks on Android 10+: the text in the query arguments (the URI's query is not read then).
                var args = new global::Android.OS.Bundle();
                args.PutString(DocumentsContract.QueryArgDisplayName, rest);
                return Rows(resolver.Query(DocumentsContract.BuildSearchDocumentsUri(AndroidDocuments.Authority, AndroidDocuments.RootId, rest)!, null, args, null)).ToJsonString();
            }
            case "recent":
                return Rows(resolver.Query(DocumentsContract.BuildRecentDocumentsUri(AndroidDocuments.Authority, AndroidDocuments.RootId)!, null, null, null, null)).ToJsonString();
            case "thumb":
            {
                using var bitmap = DocumentsContract.GetDocumentThumbnail(resolver, Doc(rest), new global::Android.Graphics.Point(128, 128), null);
                return bitmap is null ? "none" : $"{bitmap.Width}x{bitmap.Height}";
            }
            case "cache":
            {
                var catalog = AndroidDocuments.Catalog(context);
                if (long.TryParse(rest, out var maxFile))
                    catalog.Cache = new DocumentCache(catalog.Cache.Directory, AndroidDocuments.CacheBytes, maxFile);
                return new JsonObject { ["bytes"] = catalog.Cache.TotalBytes, ["files"] = catalog.Cache.Count, ["maxFile"] = catalog.Cache.MaxFileBytes }.ToJsonString();
            }
            default:
                return $"unknown docs command {op}";
        }
    }

    private static async Task WaitForSavesAsync()
    {
        for (var i = 0; i < 300 && AndroidDocuments.PendingWrites > 0; i++) await Task.Delay(100);
    }

    private static JsonArray Rows(ICursor? cursor, bool dispose = true)
    {
        var rows = new JsonArray();
        if (cursor is null) return rows;
        try
        {
            while (cursor.MoveToNext())
            {
                var row = new JsonObject();
                for (var i = 0; i < cursor.ColumnCount; i++)
                {
                    var name = cursor.GetColumnName(i)!;
                    row[name] = cursor.GetType(i) switch
                    {
                        FieldType.Integer => JsonValue.Create(cursor.GetLong(i)),
                        FieldType.Null => null,
                        _ => JsonValue.Create(cursor.GetString(i)),
                    };
                }
                rows.Add(row);
            }
        }
        finally
        {
            if (dispose) cursor.Dispose();
        }
        return rows;
    }
}
