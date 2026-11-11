using ASync.Contracts;

namespace ASync.Core.Models;

/// <summary>
/// A configured storage back-end. <see cref="UserId"/> null = shared: set up by an admin, usable by every user
/// (each under <c>users/&lt;id&gt;/</c>). The settings, secrets included, are stored encrypted with ASP.NET Data
/// Protection (<see cref="ProtectedSettings"/>), whose key ring lives in the same database — so a database dump
/// alone still exposes them; keep the database private. See AGENTS.md "Storage back-ends".
/// </summary>
public class StorageBackend
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Name { get; set; } = "";
    public StorageBackendType Type { get; set; }
    public string ProtectedSettings { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public bool? LastCheckOk { get; set; }
    public string? LastCheckError { get; set; }

    public AppUser? User { get; set; }

    public bool IsShared => UserId is null;
}
