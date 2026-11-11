using System.Text;
using ASync.Engine.Plain;
using ASync.Engine.Storage;

namespace ASync.Tests.Engine;

public class PlainStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PlainStore _store;

    public PlainStoreTests() => _store = new PlainStore(new LocalBackend(_dir.Path));

    public void Dispose() => _dir.Dispose();

    private static MemoryStream Text(string s) => new(Encoding.UTF8.GetBytes(s));

    private async Task<string> ReadAsync(string path)
    {
        await using var s = await _store.OpenReadAsync(path);
        return await new StreamReader(s).ReadToEndAsync();
    }

    [Fact]
    public async Task Put_KeepsEarlierVersions_AndFilesStayReadableOnTheBackend()
    {
        await _store.PutAsync("docs/a.txt", Text("v1"));
        await _store.PutAsync("docs/a.txt", Text("v2"));
        await _store.PutAsync("docs/a.txt", Text("v3"));

        Assert.Equal("v3", await File.ReadAllTextAsync(Path.Combine(_dir.Path, "docs/a.txt"))); // a plain file
        var versions = await _store.ListVersionsAsync("docs/a.txt");
        Assert.Equal(2, versions.Count);

        var files = new List<string>();
        await foreach (var f in _store.ListFilesAsync()) files.Add(f.Path);
        Assert.Equal(["docs/a.txt"], files);
    }

    [Fact]
    public async Task Delete_MovesToTrash_AndRestoreBringsItBack()
    {
        await _store.PutAsync("x.txt", Text("hello"));
        Assert.True(await _store.DeleteAsync("x.txt"));
        Assert.False(await _store.DeleteAsync("x.txt"));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "x.txt")));

        var trashed = Assert.Single(await _store.ListTrashAsync());
        Assert.Equal("x.txt", trashed.Path);
        await _store.RestoreAsync(trashed.StoragePath, trashed.Path);
        Assert.Equal("hello", await ReadAsync("x.txt"));
    }

    [Fact]
    public async Task RestoringAVersion_KeepsTheCurrentOneAsAVersion()
    {
        await _store.PutAsync("f.txt", Text("old"));
        await _store.PutAsync("f.txt", Text("new"));
        var old = Assert.Single(await _store.ListVersionsAsync("f.txt"));

        await _store.RestoreAsync(old.StoragePath, "f.txt");
        Assert.Equal("old", await ReadAsync("f.txt"));
        Assert.Single(await _store.ListVersionsAsync("f.txt")); // "new" is now the version
    }

    [Fact]
    public async Task CleanUp_EnforcesAgeAndCount()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.PutAsync("f.txt", Text($"v{i}"));
            await Task.Delay(2); // distinct version timestamps
        }
        await _store.PutAsync("gone.txt", Text("x"));
        await _store.DeleteAsync("gone.txt");

        var deleted = await _store.CleanUpAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(30), maxVersionsPerFile: 2);
        Assert.Equal(2, deleted);
        Assert.Equal(2, (await _store.ListVersionsAsync("f.txt")).Count);
        Assert.Single(await _store.ListTrashAsync());

        await _store.CleanUpAsync(TimeSpan.Zero, TimeSpan.Zero);
        Assert.Empty(await _store.ListVersionsAsync("f.txt"));
        Assert.Empty(await _store.ListTrashAsync());
    }

    [Fact]
    public async Task MetaPaths_AreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutAsync(".async/evil", Text("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutAsync("../outside", Text("x")));
    }
}
