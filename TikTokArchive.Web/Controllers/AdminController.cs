using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;
using TikTokArchive.Web.Services;

namespace TikTokArchive.Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly ReindexCoordinator _reindexCoordinator;

        public AdminController(
            TikTokArchiveDbContext dbContext,
            ReindexCoordinator reindexCoordinator)
        {
            _dbContext = dbContext;
            _reindexCoordinator = reindexCoordinator;
        }

        [HttpGet("config")]
        public async Task<IActionResult> GetConfig()
        {
            var config = await _dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            if (config == null)
            {
                config = new SearchIndexConfiguration { SyncIntervalMinutes = 30 };
                _dbContext.SearchIndexConfigurations.Add(config);
                await _dbContext.SaveChangesAsync();
            }

            return Ok(new
            {
                syncIntervalMinutes = config.SyncIntervalMinutes,
                lastModified = config.LastModified
            });
        }

        [HttpPost("config")]
        public async Task<IActionResult> UpdateConfig([FromBody] UpdateConfigRequest request)
        {
            var config = await _dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            if (config == null)
            {
                config = new SearchIndexConfiguration();
                _dbContext.SearchIndexConfigurations.Add(config);
            }

            config.SyncIntervalMinutes = request.SyncIntervalMinutes;
            config.LastModified = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            return Ok(new { message = "Configuration updated successfully" });
        }

        [HttpPost("reindex")]
        public IActionResult StartReindex()
        {
            if (!_reindexCoordinator.TryStart())
            {
                return Conflict(new { message = "A reindex is already running" });
            }

            return Accepted();
        }

        [HttpGet("reindex/progress")]
        public IActionResult GetReindexProgress()
        {
            var status = _reindexCoordinator.Status;
            return Ok(new
            {
                isRunning = status.IsRunning,
                processedCount = status.ProcessedCount,
                totalCount = status.TotalCount,
                percentage = status.TotalCount > 0 ? (status.ProcessedCount * 100.0 / status.TotalCount) : 0,
                startedAt = status.StartedAt,
                completedAt = status.CompletedAt,
                errorMessage = status.ErrorMessage
            });
        }

        [HttpGet("queue/status")]
        public async Task<IActionResult> GetQueueStatus()
        {
            var pendingCount = await _dbContext.SearchIndexOperations.CountAsync();
            var recentErrors = await _dbContext.SearchIndexOperations
                .Where(o => o.RetryCount > 0)
                .OrderByDescending(o => o.LastAttempt)
                .Take(10)
                .Select(o => new
                {
                    videoId = o.VideoId,
                    operationType = o.OperationType.ToString(),
                    retryCount = o.RetryCount,
                    errorMessage = o.ErrorMessage,
                    lastAttempt = o.LastAttempt
                })
                .ToListAsync();

            return Ok(new
            {
                pendingCount,
                recentErrors
            });
        }
    }

    public class UpdateConfigRequest
    {
        public int SyncIntervalMinutes { get; set; }
    }
}
