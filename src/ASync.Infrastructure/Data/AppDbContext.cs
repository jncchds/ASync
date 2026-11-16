using ASync.Contracts;
using ASync.Core.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Data;

/// <summary>
/// Also stores the ASP.NET Data Protection key ring, so OIDC sign-in cookies (and, from M1, encrypted storage
/// back-end credentials) survive container restarts.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ServerSecret> ServerSecrets => Set<ServerSecret>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<PendingDeviceCode> PendingDeviceCodes => Set<PendingDeviceCode>();
    public DbSet<StorageBackend> StorageBackends => Set<StorageBackend>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<FolderDevice> FolderDevices => Set<FolderDevice>();
    public DbSet<AlertIncident> AlertIncidents => Set<AlertIncident>();
    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();
    public DbSet<AlertSettings> AlertSettings => Set<AlertSettings>();
    public DbSet<PurgeJob> PurgeJobs => Set<PurgeJob>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<UploadSessionPart> UploadSessionParts => Set<UploadSessionPart>();
    public DbSet<FolderUsage> FolderUsages => Set<FolderUsage>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.OidcSubject).IsUnique();
            e.Property(u => u.Username).HasMaxLength(100);
            e.Property(u => u.DisplayName).HasMaxLength(100);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ServerSecret>().HasKey(s => s.Name);

        b.Entity<Device>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(100);
            e.Property(d => d.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.PushTarget).HasMaxLength(2000);
            e.Property(d => d.PushType).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.LastPushError).HasMaxLength(500);
            e.HasOne(d => d.User).WithMany(u => u.Devices).HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EnrollmentToken>(e =>
        {
            e.HasIndex(t => t.CodeHash).IsUnique();
            e.Property(t => t.Label).HasMaxLength(100);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StorageBackend>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(100);
            e.Property(s => s.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.LastCheckError).HasMaxLength(1000);
            e.HasIndex(s => s.UserId);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Folder>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(100);
            e.Property(f => f.RootPath).HasMaxLength(500);
            e.Property(f => f.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.Encryption).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.RetentionJson).HasColumnType("jsonb");
            // Database defaults give existing folders the schedule; the sentinel lets 0 ("off") be stored.
            e.Property(f => f.PruneIntervalDays).HasDefaultValue(MaintenanceDefaults.PruneIntervalDays).HasSentinel(-1);
            e.Property(f => f.CheckIntervalDays).HasDefaultValue(MaintenanceDefaults.CheckIntervalDays).HasSentinel(-1);
            e.Property(f => f.CheckReadDataPercent).HasDefaultValue(MaintenanceDefaults.CheckReadDataPercent).HasSentinel(-1);
            e.Property(f => f.LastPruneMessage).HasMaxLength(1000);
            e.Property(f => f.LastPruneBy).HasMaxLength(100);
            e.Property(f => f.LastCheckMessage).HasMaxLength(1000);
            e.Property(f => f.LastCheckBy).HasMaxLength(100);
            e.HasIndex(f => f.UserId);
            e.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            // A back-end with folders on it cannot be deleted by accident.
            e.HasOne(f => f.StorageBackend).WithMany().HasForeignKey(f => f.StorageBackendId).OnDelete(DeleteBehavior.Restrict);
        });

        // One resumable upload per device and path; gone with its folder or device (CleanupService aborts idle ones).
        b.Entity<UploadSession>(e =>
        {
            e.Property(s => s.Path).HasMaxLength(1000);
            e.Property(s => s.Handle).HasMaxLength(1000);
            e.HasIndex(s => new { s.FolderId, s.DeviceId, s.Path }).IsUnique();
            e.HasIndex(s => s.UpdatedAt);
            e.HasOne(s => s.Folder).WithMany().HasForeignKey(s => s.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Device).WithMany().HasForeignKey(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.Parts).WithOne().HasForeignKey(p => p.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        // Storage usage per folder (usage scans + uploads accepted since); gone with the folder.
        b.Entity<FolderUsage>(e =>
        {
            e.HasKey(u => u.FolderId);
            e.Property(u => u.FolderId).ValueGeneratedNever();
            e.Property(u => u.ScanError).HasMaxLength(1000);
            e.Ignore(u => u.Used);
            e.HasOne(u => u.Folder).WithOne().HasForeignKey<FolderUsage>(u => u.FolderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UploadSessionPart>(e =>
        {
            e.HasKey(p => new { p.SessionId, p.Number });
            e.Property(p => p.Sha256).HasMaxLength(64);
            e.Property(p => p.Token).HasMaxLength(200);
        });

        b.Entity<FolderDevice>(e =>
        {
            e.HasIndex(d => new { d.FolderId, d.DeviceId }).IsUnique();
            e.Property(d => d.LocalPath).HasMaxLength(1000);
            e.Property(d => d.Direction).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.LastSyncMessage).HasMaxLength(1000);
            // Memberships from before overdue detection count from the migration.
            e.Property(d => d.AttachedAt).HasDefaultValueSql("now()");
            e.HasOne(d => d.Folder).WithMany(f => f.Devices).HasForeignKey(d => d.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Device).WithMany().HasForeignKey(d => d.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AlertIncident>(e =>
        {
            e.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.FolderName).HasMaxLength(100);
            e.Property(i => i.DeviceName).HasMaxLength(100);
            e.Property(i => i.StorageBackendName).HasMaxLength(100);
            e.Property(i => i.Detail).HasMaxLength(1000);
            e.HasIndex(i => new { i.UserId, i.ResolvedAt });
            e.HasOne(i => i.Folder).WithMany().HasForeignKey(i => i.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StorageBackend>().WithMany().HasForeignKey(i => i.StorageBackendId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AlertChannel>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.LastError).HasMaxLength(1000);
            e.HasIndex(c => c.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AlertSettings>(e =>
        {
            e.HasKey(s => s.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<AlertSettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // No foreign keys: the job outlives the folder, and a deleted owner's back-ends (it keeps a copy of the settings).
        b.Entity<PurgeJob>(e =>
        {
            e.Property(j => j.OwnerName).HasMaxLength(100);
            e.Property(j => j.FolderName).HasMaxLength(100);
            e.Property(j => j.RootPath).HasMaxLength(500);
            e.Property(j => j.StorageType).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Message).HasMaxLength(1000);
            e.HasIndex(j => j.RequestedByUserId);
            e.HasIndex(j => j.Status);
        });

        b.Entity<PendingDeviceCode>(e =>
        {
            e.HasIndex(c => c.DeviceCodeHash).IsUnique();
            e.HasIndex(c => c.UserCodeHash).IsUnique();
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        });
    }
}
