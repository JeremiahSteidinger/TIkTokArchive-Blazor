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
                // Use a dummy connection string for design-time operations (migrations)
                connectionString = "Server=localhost;Database=tiktokarchive;User=root;Password=changeme;";
            }

            var optionsBuilder = new DbContextOptionsBuilder<TikTokArchiveDbContext>();
            optionsBuilder.UseMySql(connectionString, new MySqlServerVersion(new Version(10, 5, 8)));
            return new TikTokArchiveDbContext(optionsBuilder.Options);
        }
    }
}
