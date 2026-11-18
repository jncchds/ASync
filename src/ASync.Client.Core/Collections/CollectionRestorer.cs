using System.Security.Cryptography;
using ASync.Contracts;
using ASync.Engine;
using ASync.Engine.Repository;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Client.Core.Collections;

public sealed record CollectionRestoreProgress(int Done, int Total, int Restored, int Skipped, string? Current);

public sealed record CollectionRestoreResult(int Restored, int Skipped, int Total, List<string> Errors)
{
    public string Message => $"{Restored} restored, {Skipped} already there" + (Errors.Count > 0 ? $", {Errors.Count} not restored: {Errors[0]}" : "");
}

/// <summary>
/// Imports a Contacts or Calendars snapshot into the phone (task 20): every account or calendar directory of the snapshot goes into
/// <c>targetId</c> when one is given, else into the account or calendar it came from (when the phone still has it; otherwise that
/// directory is reported, not guessed). An entry already there is skipped: a contact with the same id and name, or the same name,
/// phone numbers and email addresses; an event with the same UID (or id) and title, or the same title, start, length and rule.
/// Nothing on the phone is ever changed or deleted: a restore only adds.
/// </summary>
public sealed class CollectionRestorer(Repo repository)
{
    private sealed record Group(string Directory, CollectionInfoFile? Info, List<TreeNode> Files);

    private async Task<List<Group>> WalkAsync(Snapshot snapshot, string extension, string infoFile, List<string> errors, CancellationToken ct)
    {
        var groups = new List<Group>();
        foreach (var node in (await repository.LoadTreeAsync(snapshot.Tree, ct)).Nodes)
        {
            if (node is not { Type: NodeType.Directory, Subtree: { } sub }) continue;
            var children = (await repository.LoadTreeAsync(sub, ct)).Nodes;
            CollectionInfoFile? info = null;
            if (children.FirstOrDefault(n => n.Name == infoFile && n.Type == NodeType.File) is { } json)
            {
                try { info = CollectionInfoFile.Parse(await ReadAllAsync(json, ct)); }
                catch (InvalidDataException e) { errors.Add($"{node.Name}/{infoFile}: {e.Message}"); }
            }
            groups.Add(new Group(node.Name, info,
                [.. children.Where(n => n.Type == NodeType.File && n.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name, StringComparer.Ordinal)]));
        }
        return groups;
    }

    /// <param name="targetId">The account to import everything into; null = each into its own.</param>
    public async Task<CollectionRestoreResult> RestoreContactsAsync(Snapshot snapshot, IContactsSource contacts, string? targetId,
        IProgress<CollectionRestoreProgress>? progress = null, CancellationToken ct = default)
    {
        await using var repoLock = await repository.LockAsync(exclusive: false, "restore into the contacts", ct);
        await repository.LoadIndexAsync(ct);
        var errors = new List<string>();
        var groups = await WalkAsync(snapshot, ".vcf", CollectionLayout.AccountFile, errors, ct);
        var accounts = contacts.Accounts();
        var total = groups.Sum(g => g.Files.Count);
        int done = 0, restored = 0, skipped = 0;
        var existingByAccount = new Dictionary<string, (Dictionary<string, string> Names, HashSet<string> Keys)>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var account = targetId is not null ? accounts.FirstOrDefault(a => a.Id == targetId)
                : group.Info is { } info ? accounts.FirstOrDefault(a => a.Id == info.Id) ?? Single(accounts.Where(a => !a.ReadOnly && a.Name == info.Name && a.Type == info.Type))
                : null;
            if (account is null || account.ReadOnly)
            {
                Note(errors, account is null
                    ? targetId is not null ? $"the account to restore into is not on this phone any more" : $"{group.Info?.Name ?? group.Directory}: that account is not on this phone; choose one to restore into"
                    : $"{account.Name} does not take new contacts");
                done += group.Files.Count;
                continue;
            }
            if (!existingByAccount.TryGetValue(account.Id, out var existing))
            {
                existing = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                foreach (var s in contacts.Contacts(new HashSet<string>([account.Id], StringComparer.Ordinal)))
                {
                    if (contacts.Read(s.Id, withPhoto: false) is not { } c) continue;
                    existing.Names[c.Id] = VCard.FormattedName(c);
                    existing.Keys.Add(VCard.ContentKey(c));
                }
                existingByAccount[account.Id] = existing;
            }
            foreach (var file in group.Files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CollectionRestoreProgress(done, total, restored, skipped, file.Name));
                try
                {
                    foreach (var card in VCard.Parse(await ReadAllAsync(file, ct)))
                    {
                        var name = VCard.FormattedName(card);
                        var key = VCard.ContentKey(card);
                        if ((existing.Names.TryGetValue(card.Id, out var there) && there == name) || existing.Keys.Contains(key)) skipped++;
                        else
                        {
                            await contacts.InsertAsync(account, card with { AccountId = account.Id }, ct);
                            existing.Keys.Add(key);
                            restored++;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException
                                             or InvalidOperationException or NotSupportedException or FormatException)
                {
                    Note(errors, $"{group.Directory}/{file.Name}: {e.Message}");
                }
                done++;
            }
        }
        progress?.Report(new CollectionRestoreProgress(done, total, restored, skipped, null));
        return new CollectionRestoreResult(restored, skipped, total, errors);
    }

    /// <param name="targetId">The calendar to import everything into; null = each into its own.</param>
    public async Task<CollectionRestoreResult> RestoreCalendarsAsync(Snapshot snapshot, ICalendarSource calendars, string? targetId,
        IProgress<CollectionRestoreProgress>? progress = null, CancellationToken ct = default)
    {
        await using var repoLock = await repository.LockAsync(exclusive: false, "restore into the calendars", ct);
        await repository.LoadIndexAsync(ct);
        var errors = new List<string>();
        var groups = await WalkAsync(snapshot, ".ics", CollectionLayout.CalendarFile, errors, ct);
        var all = calendars.Calendars();
        var total = groups.Sum(g => g.Files.Count);
        int done = 0, restored = 0, skipped = 0;
        var existingByCalendar = new Dictionary<string, (Dictionary<string, string?> Ids, HashSet<string> Keys)>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var calendar = targetId is not null ? all.FirstOrDefault(c => c.Id == targetId)
                : group.Info is { } info ? all.FirstOrDefault(c => c.Id == info.Id)
                    ?? Single(all.Where(c => !c.ReadOnly && c.Name == info.Name && c.AccountName == info.Account && c.AccountType == info.AccountType))
                : null;
            if (calendar is null || calendar.ReadOnly)
            {
                Note(errors, calendar is null
                    ? targetId is not null ? "the calendar to restore into is not on this phone any more" : $"{group.Info?.Name ?? group.Directory}: that calendar is not on this phone; choose one to restore into"
                    : $"{calendar.Name} does not take new events");
                done += group.Files.Count;
                continue;
            }
            if (!existingByCalendar.TryGetValue(calendar.Id, out var existing))
            {
                existing = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                foreach (var e in calendars.Events(new HashSet<string>([calendar.Id], StringComparer.Ordinal)))
                {
                    existing.Ids[e.Id] = e.Title;
                    if (e.Uid is { Length: > 0 } uid) existing.Ids[uid] = e.Title;
                    existing.Keys.Add(ICalendar.ContentKey(e));
                }
                existingByCalendar[calendar.Id] = existing;
            }
            foreach (var file in group.Files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CollectionRestoreProgress(done, total, restored, skipped, file.Name));
                try
                {
                    foreach (var ev in ICalendar.Parse(await ReadAllAsync(file, ct)))
                    {
                        var uid = ev.Uid ?? ev.Id;
                        var key = ICalendar.ContentKey(ev);
                        if ((existing.Ids.TryGetValue(uid, out var title) && title == ev.Title) || existing.Keys.Contains(key)) skipped++;
                        else
                        {
                            await calendars.InsertAsync(calendar, ev with { CalendarId = calendar.Id, Uid = uid }, ct);
                            existing.Ids[uid] = ev.Title;
                            existing.Keys.Add(key);
                            restored++;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException
                                             or InvalidOperationException or NotSupportedException or FormatException)
                {
                    Note(errors, $"{group.Directory}/{file.Name}: {e.Message}");
                }
                done++;
            }
        }
        progress?.Report(new CollectionRestoreProgress(done, total, restored, skipped, null));
        return new CollectionRestoreResult(restored, skipped, total, errors);
    }

    /// <summary>The account or calendar directories of a snapshot with what their JSON says (the app's restore choice).</summary>
    public async Task<List<(string Directory, CollectionInfoFile? Info, int Files)>> GroupsAsync(Snapshot snapshot, FolderKind kind, CancellationToken ct = default)
    {
        await repository.LoadIndexAsync(ct);
        var errors = new List<string>();
        var groups = kind == FolderKind.Contacts
            ? await WalkAsync(snapshot, ".vcf", CollectionLayout.AccountFile, errors, ct)
            : await WalkAsync(snapshot, ".ics", CollectionLayout.CalendarFile, errors, ct);
        return [.. groups.Select(g => (g.Directory, g.Info, g.Files.Count))];
    }

    /// <summary>
    /// The one account or calendar of that name and type, when the id the snapshot names is not on the phone: iOS gives containers and
    /// calendars new identifiers on another iPhone or after a reset, while "iCloud" stays "iCloud". Two candidates: none (not guessed).
    /// </summary>
    private static T? Single<T>(IEnumerable<T> candidates) where T : class
    {
        var list = candidates.Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }

    private static void Note(List<string> errors, string message)
    {
        if (errors.Count < 100 && !errors.Contains(message)) errors.Add(message);
    }

    private async Task<byte[]> ReadAllAsync(TreeNode node, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        foreach (var id in node.Content ?? []) buffer.Write(await repository.ReadBlobAsync(id, ct));
        return buffer.ToArray();
    }
}
