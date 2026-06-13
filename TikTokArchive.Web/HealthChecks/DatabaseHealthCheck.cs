using Microsoft.Extensions.Diagnostics.HealthChecks;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.HealthChecks
{
    public class DatabaseHealthCheck : IHealthCheck
    {
        private readonly TikTokArchiveDbContext _dbContext;

        public DatabaseHealthCheck(TikTokArchiveDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _dbContext.Database.CanConnectAsync(cancellationToken)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("Cannot connect to MySQL");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Cannot connect to MySQL", ex);
            }
        }
    }
}
