using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bast.Application.Persistence;

public sealed class BastDbContext(DbContextOptions<BastDbContext> options) : DbContext(options)
{
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Publication> Publications => Set<Publication>();
    public DbSet<PublicationAttempt> PublicationAttempts => Set<PublicationAttempt>();
    public DbSet<ImportIssue> ImportIssues => Set<ImportIssue>();
    public DbSet<ScheduleSettings> ScheduleSettings => Set<ScheduleSettings>();
    public DbSet<PlatformAccount> PlatformAccounts => Set<PlatformAccount>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ProductionJob> ProductionJobs => Set<ProductionJob>();
    public DbSet<ProductionStep> ProductionSteps => Set<ProductionStep>();
    public DbSet<ProductionLog> ProductionLogs => Set<ProductionLog>();
    public DbSet<ProductionControl> ProductionControl => Set<ProductionControl>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Todas as datas são UTC; marca o Kind na leitura para que a API as serialize com "Z".
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Video>(e =>
        {
            e.ToTable("videos");
            e.HasKey(v => v.Id);
            e.Property(v => v.State).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Lane).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.CurrentFolder).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.PendingMoveFolder).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.ContentHash).HasMaxLength(64);
            // Impede registrar duas vezes o mesmo conteúdo, exceto em reimportação intencional (nova geração).
            e.HasIndex(v => new { v.ContentHash, v.Generation }).IsUnique();
            e.HasIndex(v => v.State);
        });

        modelBuilder.Entity<Publication>(e =>
        {
            e.ToTable("publications");
            e.HasKey(p => p.Id);
            e.Property(p => p.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.State).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Version).IsConcurrencyToken();
            e.HasOne(p => p.Video).WithMany(v => v.Publications).HasForeignKey(p => p.VideoId).OnDelete(DeleteBehavior.Restrict);
            // Uma única publicação por vídeo em cada plataforma.
            e.HasIndex(p => new { p.VideoId, p.Platform }).IsUnique();
            e.HasIndex(p => new { p.State, p.ScheduledAtUtc });
            e.HasIndex(p => p.IdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<PublicationAttempt>(e =>
        {
            e.ToTable("publication_attempts");
            e.HasKey(a => a.Id);
            e.Property(a => a.ResultState).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Publication>().WithMany(p => p.Attempts).HasForeignKey(a => a.PublicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.PublicationId, a.Number }).IsUnique();
        });

        modelBuilder.Entity<ImportIssue>(e =>
        {
            e.ToTable("import_issues");
            e.HasKey(i => i.Id);
            e.Property(i => i.Code).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(i => new { i.FolderName, i.Code, i.ResolvedAtUtc });
        });

        modelBuilder.Entity<PlatformAccount>(e =>
        {
            e.ToTable("platform_accounts");
            e.HasKey(a => a.Platform);
            e.Property(a => a.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(n => n.Id);
            e.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30);
            e.Property(n => n.DedupKey).HasMaxLength(100);
            e.HasIndex(n => n.DedupKey).IsUnique();
            e.HasIndex(n => new { n.SentAtUtc, n.DiscardedAtUtc, n.CreatedAtUtc });
        });

        modelBuilder.Entity<ProductionJob>(e =>
        {
            e.ToTable("production_jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(j => j.Fingerprint).HasMaxLength(300);
            e.HasIndex(j => j.Fingerprint).IsUnique();
            e.HasIndex(j => j.Status);
            e.Ignore(j => j.IsFinished);
        });

        modelBuilder.Entity<ProductionStep>(e =>
        {
            e.ToTable("production_steps");
            e.HasKey(s => s.Id);
            e.Property(s => s.State).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Name).HasMaxLength(60);
            e.HasOne<ProductionJob>().WithMany(j => j.Steps).HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.JobId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<ProductionLog>(e =>
        {
            e.ToTable("production_logs");
            e.HasKey(l => l.Id);
            e.Property(l => l.Level).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(l => new { l.JobId, l.AtUtc });
        });

        modelBuilder.Entity<ProductionControl>(e =>
        {
            e.ToTable("production_control");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<ScheduleSettings>(e =>
        {
            e.ToTable("schedule_settings");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.MissedPolicy).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Mode).HasConversion<string>().HasMaxLength(20);
            e.Ignore(s => s.Platforms);
            e.Ignore(s => s.Days);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
