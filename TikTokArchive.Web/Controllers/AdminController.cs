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
        private readonly ITranscriptionService _transcriptionService;

        public AdminController(
            TikTokArchiveDbContext dbContext,
            ReindexCoordinator reindexCoordinator,
            ITranscriptionService transcriptionService)
        {
            _dbContext = dbContext;
            _reindexCoordinator = reindexCoordinator;
            _transcriptionService = transcriptionService;
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

        [HttpGet("transcription/status")]
        public async Task<IActionResult> GetTranscriptionStatus()
        {
            var counts = await _transcriptionService.GetStatusCountsAsync();

            var recentFailures = await _dbContext.Videos
                .Where(v => v.TranscriptStatus == TranscriptStatus.Failed)
                .OrderByDescending(v => v.TranscriptLastAttempt)
                .Take(10)
                .Select(v => new
                {
                    videoId = v.TikTokVideoId,
                    retryCount = v.TranscriptRetryCount,
                    errorMessage = v.TranscriptErrorMessage,
                    lastAttempt = v.TranscriptLastAttempt
                })
                .ToListAsync();

            return Ok(new
            {
                counts = counts.ToDictionary(c => c.Key.ToString(), c => c.Value),
                recentFailures
            });
        }

        [HttpPost("transcription/queue/{videoId}")]
        public async Task<IActionResult> QueueTranscription(string videoId)
        {
            var queued = await _transcriptionService.QueueAsync(videoId);
            return queued
                ? Ok(new { message = $"Queued {videoId} for transcription" })
                : NotFound(new { message = $"Video {videoId} not found" });
        }

        [HttpPost("transcription/{videoId}/delete")]
        public async Task<IActionResult> DeleteTranscript(string videoId)
        {
            var deleted = await _transcriptionService.DeleteTranscriptAsync(videoId);
            return deleted
                ? Ok(new { message = $"Deleted transcript for {videoId}" })
                : NotFound(new { message = $"Video {videoId} not found" });
        }

        [HttpPost("transcription/retry-failed")]
        public async Task<IActionResult> RetryFailedTranscriptions()
        {
            var count = await _transcriptionService.RetryFailedAsync();
            return Ok(new { message = $"Requeued {count} failed transcription(s)", count });
        }

        [HttpPost("transcription/backfill")]
        public async Task<IActionResult> BackfillTranscriptions()
        {
            var count = await _transcriptionService.BackfillAsync();
            return Ok(new { message = $"Queued {count} video(s) for transcription", count });
        }
    }

    public class UpdateConfigRequest
    {
        public int SyncIntervalMinutes { get; set; }
    }
}
