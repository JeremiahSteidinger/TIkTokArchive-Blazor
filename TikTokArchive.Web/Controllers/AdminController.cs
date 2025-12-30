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
        private readonly ISearchService _searchService;
        private readonly SearchIndexQueue _queue;
        private readonly ILogger<AdminController> _logger;
        private static readonly Dictionary<string, ReindexProgress> _reindexProgress = new();

        public AdminController(
            TikTokArchiveDbContext dbContext,
            ISearchService searchService,
            SearchIndexQueue queue,
            ILogger<AdminController> logger)
        {
            _dbContext = dbContext;
            _searchService = searchService;
            _queue = queue;
            _logger = logger;
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
        public async Task<IActionResult> StartReindex()
        {
            var sessionId = Guid.NewGuid().ToString();
            
            _reindexProgress[sessionId] = new ReindexProgress
            {
                IsRunning = true,
                ProcessedCount = 0,
                TotalCount = await _dbContext.Videos.CountAsync(),
                StartedAt = DateTime.UtcNow
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<int>(count =>
                    {
                        if (_reindexProgress.ContainsKey(sessionId))
                        {
                            _reindexProgress[sessionId].ProcessedCount = count;
                        }
                    });

                    await _searchService.BulkReindexAsync(progress);

                    if (_reindexProgress.ContainsKey(sessionId))
                    {
                        _reindexProgress[sessionId].IsRunning = false;
                        _reindexProgress[sessionId].CompletedAt = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during bulk reindex");
                    if (_reindexProgress.ContainsKey(sessionId))
                    {
                        _reindexProgress[sessionId].IsRunning = false;
                        _reindexProgress[sessionId].ErrorMessage = ex.Message;
                    }
                }
            });

            return Ok(new { sessionId });
        }

        [HttpGet("reindex/progress/{sessionId}")]
        public IActionResult GetReindexProgress(string sessionId)
        {
            if (!_reindexProgress.ContainsKey(sessionId))
            {
                return NotFound();
            }

            var progress = _reindexProgress[sessionId];
            return Ok(new
            {
                isRunning = progress.IsRunning,
                processedCount = progress.ProcessedCount,
                totalCount = progress.TotalCount,
                percentage = progress.TotalCount > 0 ? (progress.ProcessedCount * 100.0 / progress.TotalCount) : 0,
                startedAt = progress.StartedAt,
                completedAt = progress.CompletedAt,
                errorMessage = progress.ErrorMessage
            });
        }

        [HttpGet("queue/status")]
        public async Task<IActionResult> GetQueueStatus()
        {
            var pendingCount = await _queue.GetPendingCountAsync();
            var recentErrors = await _dbContext.SearchIndexOperations
                .Where(o => o.RetryCount >= 3)
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

    public class ReindexProgress
    {
        public bool IsRunning { get; set; }
        public int ProcessedCount { get; set; }
        public int TotalCount { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
