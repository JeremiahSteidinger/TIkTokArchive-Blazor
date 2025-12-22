using Microsoft.EntityFrameworkCore;

namespace TikTokArchive.Entities
{
    public class TikTokArchiveDbContext(DbContextOptions<TikTokArchiveDbContext> options) : DbContext(options)
    {
        public DbSet<Video> Videos { get; set; }
        public DbSet<Creator> Creators { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<VideoTag> VideoTags { get; set; }

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
        }
    }
}
