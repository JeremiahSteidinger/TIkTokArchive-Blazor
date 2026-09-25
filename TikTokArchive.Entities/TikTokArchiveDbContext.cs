using Microsoft.EntityFrameworkCore;

namespace TikTokArchive.Entities
{
    public class TikTokArchiveDbContext(DbContextOptions<TikTokArchiveDbContext> options) : DbContext(options)
    {
        public DbSet<Video> Videos { get; set; }
        public DbSet<Creator> Creators { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<VideoTag> VideoTags { get; set; }
        public DbSet<SearchIndexOperation> SearchIndexOperations { get; set; }
        public DbSet<SearchIndexConfiguration> SearchIndexConfigurations { get; set; }
        public DbSet<LocalImportLog> LocalImportLogs { get; set; }
        public DbSet<AiProviderSettings> AiProviderSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Creator -> Videos (one-to-many)
            modelBuilder.Entity<Creator>()
                .HasMany(c => c.Videos)
                .WithOne(v => v.Creator);

            // Video -> VideoTags (one-to-many)
            modelBuilder.Entity<Video>()
                .HasMany(v => v.Tags)
                .WithOne(vt => vt.Video)
                .HasForeignKey(vt => vt.VideoId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tag -> VideoTags (one-to-many)
            modelBuilder.Entity<Tag>()
                .HasMany(t => t.VideoTags)
                .WithOne(vt => vt.Tag)
                .HasForeignKey(vt => vt.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint on tag name
            modelBuilder.Entity<Tag>()
                .HasIndex(t => t.Name)
                .IsUnique();

            // Composite unique constraint on VideoId + TagId (prevent duplicates)
            modelBuilder.Entity<VideoTag>()
                .HasIndex(vt => new { vt.VideoId, vt.TagId })
                .IsUnique();

            modelBuilder.Entity<Video>()
                .Property(v => v.AddedToApp)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Transcripts can be long (minutes of speech), so LONGTEXT rather than TEXT.
            modelBuilder.Entity<Video>()
                .Property(v => v.Transcript)
                .HasColumnType("LONGTEXT");

            // Covering index for the transcription worker's claim query
            // (WHERE TranscriptStatus IN (Pending, Failed) ORDER BY ...).
            modelBuilder.Entity<Video>()
                .HasIndex(v => new { v.TranscriptStatus, v.TranscriptLastAttempt });

            // Summaries are short (3–5 sentences) but LONGTEXT matches the Transcript precedent
            // and costs nothing.
            modelBuilder.Entity<Video>()
                .Property(v => v.Summary)
                .HasColumnType("LONGTEXT");

            // Covering index for the AI-enrichment worker's claim query
            // (WHERE AiSummaryStatus IN (Pending, Failed) ORDER BY ...).
            modelBuilder.Entity<Video>()
                .HasIndex(v => new { v.AiSummaryStatus, v.AiSummaryLastAttempt });

            // Supports the admin "re-queue everything a given provider enriched" query
            // (WHERE AiSummaryProvider = ...).
            modelBuilder.Entity<Video>()
                .HasIndex(v => v.AiSummaryProvider);

            // SearchIndexOperation - created timestamp default
            modelBuilder.Entity<SearchIndexOperation>()
                .Property(s => s.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // SearchIndexConfiguration - last modified default
            modelBuilder.Entity<SearchIndexConfiguration>()
                .Property(s => s.LastModified)
                .HasDefaultValueSql("CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP");

            // AiProviderSettings - last modified default
            modelBuilder.Entity<AiProviderSettings>()
                .Property(s => s.LastModified)
                .HasDefaultValueSql("CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP");

            // LocalImportLog - imported timestamp default
            modelBuilder.Entity<LocalImportLog>()
                .Property(l => l.ImportedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
        }
    }
}
