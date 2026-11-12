using ASync.Engine.Repository;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Engine;

public class SnapshotIdTests
{
    private static Snapshot Make(string id) => new() { Id = id, Time = DateTimeOffset.UtcNow, Tree = default, Source = "test" };

    [Fact]
    public void Short_ids_of_snapshots_made_together_differ()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => Repo.NewSnapshotId()).ToList();
        Assert.Equal(50, ids.Select(Repo.ShortSnapshotId).Distinct().Count());
        Assert.All(ids, id => Assert.Equal(8, Repo.ShortSnapshotId(id).Length));
    }

    [Fact]
    public void Find_matches_prefix_suffix_and_full_id()
    {
        var a = Make("0195aaaa0000700080000000deadbeef");
        var b = Make("0195aaaa0000700080000000cafef00d");
        Snapshot[] all = [a, b];

        Assert.Equal([a], Repo.FindSnapshots(all, "deadbeef"));
        Assert.Equal([b], Repo.FindSnapshots(all, "CAFEF00D"));
        Assert.Equal([a], Repo.FindSnapshots(all, a.Id));
        Assert.Equal([a], Repo.FindSnapshots(all, "0195aaaa-0000-7000-8000-0000deadbeef"));
        Assert.Equal(2, Repo.FindSnapshots(all, "0195aaaa").Count);
        Assert.Empty(Repo.FindSnapshots(all, "12345678"));
        Assert.Empty(Repo.FindSnapshots(all, " "));
    }
}
