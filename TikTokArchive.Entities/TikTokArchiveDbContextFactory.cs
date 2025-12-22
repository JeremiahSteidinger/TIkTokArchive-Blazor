using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace TikTokArchive.Entities
{
    public class TikTokArchiveDbContextFactory : IDesignTimeDbContextFactory<TikTokArchiveDbContext>
    {
        public TikTokArchiveDbContext CreateDbContext(string[] args)
        {
            var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("MYSQL_CONNECTION_STRING environment variable is not set.");
            }

            var optionsBuilder = new DbContextOptionsBuilder<TikTokArchiveDbContext>();
            optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
            return new TikTokArchiveDbContext(optionsBuilder.Options);
        }
    }
}
