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
            e.HasIndex(f => f.UserId);
            e.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            // A back-end with folders on it cannot be deleted by accident.
            e.HasOne(f => f.StorageBackend).WithMany().HasForeignKey(f => f.StorageBackendId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FolderDevice>(e =>
        {
            e.HasIndex(d => new { d.FolderId, d.DeviceId }).IsUnique();
            e.Property(d => d.LocalPath).HasMaxLength(1000);
            e.Property(d => d.Direction).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.LastSyncMessage).HasMaxLength(1000);
            e.HasOne(d => d.Folder).WithMany(f => f.Devices).HasForeignKey(d => d.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Device).WithMany().HasForeignKey(d => d.DeviceId).OnDelete(DeleteBehavior.Cascade);
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
