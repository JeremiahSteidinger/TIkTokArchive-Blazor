using Microsoft.EntityFrameworkCore;

namespace TikTokArchive.Entities
{
    public class TikTokArchiveDbContext(DbContextOptions<TikTokArchiveDbContext> options) : DbContext(options)
    {
        public DbSet<Video> Videos { get; set; }
        public DbSet<Creator> Creators { get; set; }
        public DbSet<VideoTag> VideoTags { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Creator>()
                .HasMany(c => c.Videos)
                .WithOne(v => v.Creator);

            modelBuilder.Entity<Video>()
                .HasMany(v => v.Tags)
                .WithOne(t => t.Video);

            modelBuilder.Entity<Video>()
                .Property(v => v.AddedToApp)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
        }
    }
}
