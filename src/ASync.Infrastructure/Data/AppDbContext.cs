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
