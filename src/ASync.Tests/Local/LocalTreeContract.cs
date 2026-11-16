using System.Text;
using ASync.Engine.Local;

namespace ASync.Tests.Local;

/// <summary>
/// What every <see cref="ILocalTree"/> must do, as plain checks with no test framework: xUnit runs them against
/// <see cref="FileSystemTree"/> (<c>LocalTreeTests</c>), and the Android app runs the same file (linked into it) against
/// its SAF tree on a device or emulator (<c>scripts/smoke-android.sh</c>, automation command <c>selftest</c>). Each check
/// gets an empty tree and throws <see cref="ContractException"/> when it fails.
/// </summary>
public static class LocalTreeContract
{
    public sealed class ContractException(string message) : Exception(message);

    public static readonly IReadOnlyList<(string Name, Action<ILocalTree> Check)> Checks =
    [
        ("root exists and starts empty", RootExistsAndEmpty),
        ("write, stat and read a file", WriteStatRead),
        ("append continues a file", AppendContinues),
        ("writing creates parent directories", CreatesParents),
        ("list shows files and directories", ListShowsBoth),
        ("stat of a missing path is null", StatMissing),
        ("move renames in place", MoveRenames),
        ("move into another directory", MoveAcross),
        ("move refuses an existing target unless overwriting", MoveOverwrite),
        ("delete file and directory", Delete),
        ("deleting what is missing does nothing", DeleteMissing),
        ("modification time set or reported unsupported", ModifiedTime),
        ("listing a missing directory throws", ListMissingThrows),
        ("names with spaces and unicode", OddNames),
    ];

    /// <summary>Runs every check, each in a fresh subdirectory made by <paramref name="freshTree"/>; returns (name, null) or (name, error).</summary>
    public static List<(string Name, string? Error)> RunAll(Func<string, ILocalTree> freshTree)
    {
        var results = new List<(string, string?)>();
        var n = 0;
        foreach (var (name, check) in Checks)
        {
            try
            {
                check(freshTree($"case{++n}"));
                results.Add((name, null));
            }
            catch (Exception e)
            {
                results.Add((name, $"{e.GetType().Name}: {e.Message}"));
            }
        }
        return results;
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new ContractException(what);
    }

    private static void Write(ILocalTree tree, string path, string content)
    {
        using var s = tree.OpenAppend(path);
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes);
    }

    private static string ReadText(ILocalTree tree, string path)
    {
        using var s = tree.OpenRead(path);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static void RootExistsAndEmpty(ILocalTree tree)
    {
        True(tree.Exists(), "the root exists");
        True(tree.List("").Count == 0, "a new tree is empty");
        True(tree.Stat("") is { IsDirectory: true }, "the root is a directory");
    }

    private static void WriteStatRead(ILocalTree tree)
    {
        Write(tree, "a.txt", "hello");
        var e = tree.Stat("a.txt");
        True(e is { IsFile: true, Name: "a.txt", Size: 5 }, $"stat says a 5-byte file (got {e})");
        True(e!.Modified > DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds(), "a recent modification time");
        True(ReadText(tree, "a.txt") == "hello", "reads back what was written");
    }

    private static void AppendContinues(ILocalTree tree)
    {
        Write(tree, "p.part", "abc");
        Write(tree, "p.part", "def");
        True(ReadText(tree, "p.part") == "abcdef", "the second write goes at the end");
        True(tree.Stat("p.part")!.Size == 6, "the size grows");
    }

    private static void CreatesParents(ILocalTree tree)
    {
        Write(tree, "x/y/z.txt", "deep");
        True(tree.Stat("x") is { IsDirectory: true }, "x is a directory");
        True(tree.Stat("x/y") is { IsDirectory: true }, "x/y is a directory");
        True(ReadText(tree, "x/y/z.txt") == "deep", "the file is there");
    }

    private static void ListShowsBoth(ILocalTree tree)
    {
        Write(tree, "f.txt", "1");
        Write(tree, "d/g.txt", "22");
        var names = tree.List("").OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => $"{e.Name}:{e.Kind}").ToList();
        True(names.SequenceEqual(["d:Directory", "f.txt:File"]), $"root lists d and f.txt (got {string.Join(", ", names)})");
        var inner = tree.List("d");
        True(inner.Count == 1 && inner[0] is { Name: "g.txt", Size: 2, IsFile: true }, "d lists g.txt with its size");
    }

    private static void StatMissing(ILocalTree tree)
    {
        True(tree.Stat("nope.txt") is null, "a missing file");
        True(tree.Stat("no/such/thing") is null, "a missing nested path");
    }

    private static void MoveRenames(ILocalTree tree)
    {
        Write(tree, "old.txt", "move me");
        tree.Move("old.txt", "new name.txt", overwrite: false);
        True(tree.Stat("old.txt") is null, "the old name is gone");
        True(ReadText(tree, "new name.txt") == "move me", "the new name has the content");
    }

    private static void MoveAcross(ILocalTree tree)
    {
        Write(tree, ".async/tmp/k.part", "payload");
        tree.Move(".async/tmp/k.part", "docs/sub/report.pdf", overwrite: true);
        True(tree.Stat(".async/tmp/k.part") is null, "the temp file is gone");
        True(ReadText(tree, "docs/sub/report.pdf") == "payload", "the target has the content (parents created)");
        Write(tree, "docs/sub/report.pdf.2", "x");
        tree.Move("docs/sub/report.pdf.2", ".async/trash/20260101T000000Z/docs/sub/report.pdf.2", overwrite: true);
        True(tree.Stat(".async/trash/20260101T000000Z/docs/sub/report.pdf.2") is { IsFile: true }, "moved into a new trash directory");
    }

    private static void MoveOverwrite(ILocalTree tree)
    {
        Write(tree, "a", "A");
        Write(tree, "b", "B");
        var refused = false;
        try { tree.Move("a", "b", overwrite: false); }
        catch (IOException) { refused = true; }
        True(refused, "an existing target is refused without overwrite");
        True(ReadText(tree, "a") == "A" && ReadText(tree, "b") == "B", "nothing changed");
        tree.Move("a", "b", overwrite: true);
        True(tree.Stat("a") is null && ReadText(tree, "b") == "A", "overwrite replaces the target");
    }

    private static void Delete(ILocalTree tree)
    {
        Write(tree, "f", "1");
        Write(tree, "d/e/f", "2");
        tree.DeleteFile("f");
        True(tree.Stat("f") is null, "the file is gone");
        tree.DeleteDirectory("d");
        True(tree.Stat("d") is null, "the directory and its contents are gone");
        True(tree.List("").Count == 0, "the tree is empty again");
    }

    private static void DeleteMissing(ILocalTree tree)
    {
        tree.DeleteFile("missing");
        tree.DeleteDirectory("missing/dir");
    }

    private static void ModifiedTime(ILocalTree tree)
    {
        Write(tree, "t.txt", "time");
        var wanted = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds() / 1000 * 1000;
        var set = tree.CanSetModified && tree.TrySetModified("t.txt", wanted);
        var now = tree.Stat("t.txt")!.Modified;
        if (set) True(Math.Abs(now - wanted) < 2000, $"the time was set (wanted {wanted}, got {now})");
        else True(!tree.CanSetModified, "a tree that could not set the time says so afterwards");
        True(ReadText(tree, "t.txt") == "time", "setting the time leaves the content alone");
    }

    private static void ListMissingThrows(ILocalTree tree)
    {
        var threw = false;
        try { tree.List("not-here"); }
        catch (DirectoryNotFoundException) { threw = true; }
        True(threw, "a missing directory is an error, never an empty list");
    }

    private static void OddNames(ILocalTree tree)
    {
        Write(tree, "Фото 2026/über café (1).txt", "ok");
        True(tree.List("Фото 2026").Single().Name == "über café (1).txt", "the name survives as it is");
        True(ReadText(tree, "Фото 2026/über café (1).txt") == "ok", "and reads back");
    }
}
