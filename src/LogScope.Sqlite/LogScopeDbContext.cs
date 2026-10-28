using Microsoft.EntityFrameworkCore;
using LogScope.Domain;

namespace LogScope.Sqlite;

/// <summary>
/// 調査メタデータ用の SQLite データベースコンテキスト。
/// ログ本文は元ファイルから読むため、ここに保存するのはメタデータのみ。
/// </summary>
public class LogScopeDbContext : DbContext
{
    public DbSet<LogSource> LogSources => Set<LogSource>();
    public DbSet<EventReference> EventReferences => Set<EventReference>();
    public DbSet<Annotation> Annotations => Set<Annotation>();
    public DbSet<AnnotationEvidence> AnnotationEvidences => Set<AnnotationEvidence>();
    public DbSet<Investigation> Investigations => Set<Investigation>();
    public DbSet<InvestigationSourceLink> InvestigationSourceLinks => Set<InvestigationSourceLink>();
    public DbSet<InvestigationSessionState> InvestigationSessionStates => Set<InvestigationSessionState>();

    public LogScopeDbContext(DbContextOptions<LogScopeDbContext> options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // 派生クラスでオプションが設定されない場合のフォールバック。
        // 通常は依存性注入で Options を渡す。
        if (!optionsBuilder.IsConfigured)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LogScope",
                "logscope.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            optionsBuilder.UseSqlite($"Data Source={path}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LogSource>(e =>
        {
            e.HasKey(x => x.SourceId);
            e.Property(x => x.FilePath).IsRequired().HasMaxLength(2048);
            e.Property(x => x.FileFingerprint).HasMaxLength(128);
            e.Property(x => x.Encoding).HasMaxLength(32);
            e.HasIndex(x => x.LastModified);
            e.HasIndex(x => x.FileFingerprint);
        });

        modelBuilder.Entity<EventReference>(e =>
        {
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasMaxLength(64);
            e.Property(x => x.Message).HasMaxLength(4096);
            e.Property(x => x.EventType).HasMaxLength(128);
            e.Property(x => x.CorrelationKeys).HasMaxLength(4096);
            e.Property(x => x.DeviceId).HasMaxLength(128);
            e.HasOne<LogSource>()
                .WithMany()
                .HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SourceId);
            e.HasIndex(x => x.NormalizedTime);
            e.HasIndex(x => x.LineNumber);
        });

        modelBuilder.Entity<Annotation>(e =>
        {
            e.HasKey(x => x.AnnotationId);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Comment).HasMaxLength(65536);
            e.HasOne<Investigation>()
                .WithMany(x => x.Annotations)
                .HasForeignKey(x => x.InvestigationId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.InvestigationId);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<AnnotationEvidence>(e =>
        {
            e.HasKey(x => new { x.AnnotationId, x.EventId, x.SourceId });
            e.HasOne(x => x.Annotation)
                .WithMany(x => x.EvidenceItems)
                .HasForeignKey(x => x.AnnotationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => new { x.EventId, x.SourceId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Investigation>(e =>
        {
            e.HasKey(x => x.InvestigationId);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Summary).HasMaxLength(2048);
            e.Property(x => x.IncidentDescription).HasMaxLength(8192);
            e.Property(x => x.Hypothesis).HasMaxLength(8192);
            e.Property(x => x.ConfirmedCause).HasMaxLength(8192);
            e.Property(x => x.ActionsTaken).HasMaxLength(8192);
            e.Property(x => x.UnresolvedItems).HasMaxLength(8192);
            e.Property(x => x.Conclusion).HasMaxLength(8192);
            e.HasIndex(x => x.UpdatedAt);
        });

        modelBuilder.Entity<InvestigationSourceLink>(e =>
        {
            e.ToTable("InvestigationSourceLink");
            e.HasKey(x => new { x.InvestigationId, x.SourceId });
            e.HasOne<Investigation>().WithMany().HasForeignKey(x => x.InvestigationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<LogSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.InvestigationId, x.SortOrder });
        });

        modelBuilder.Entity<InvestigationSessionState>(e =>
        {
            e.ToTable("InvestigationSessionState");
            e.HasKey(x => x.InvestigationId);
            e.Property(x => x.StateVersion).IsRequired();
            e.Property(x => x.StateJson).IsRequired();
            e.HasOne<Investigation>().WithMany().HasForeignKey(x => x.InvestigationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
