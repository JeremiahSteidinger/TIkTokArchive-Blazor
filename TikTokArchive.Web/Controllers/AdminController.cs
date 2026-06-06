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
        private readonly RabbitMQService? _rabbitMQService;
        private readonly SpeechToTextProviderFactory? _providerFactory;
        private readonly ILogger<AdminController> _logger;
        private static readonly Dictionary<string, ReindexProgress> _reindexProgress = new();

        public AdminController(
            TikTokArchiveDbContext dbContext,
            ISearchService searchService,
            SearchIndexQueue queue,
            ILogger<AdminController> logger,
            RabbitMQService? rabbitMQService = null,
            SpeechToTextProviderFactory? providerFactory = null)
        {
            _dbContext = dbContext;
            _searchService = searchService;
            _queue = queue;
            _rabbitMQService = rabbitMQService;
            _providerFactory = providerFactory;
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

        [HttpGet("transcription/status")]
        public async Task<IActionResult> GetTranscriptionStatus()
        {
            var isEnabled = _providerFactory != null && await _providerFactory.IsEnabledAsync();
            var provider = _providerFactory != null ? await _providerFactory.GetConfiguredProviderTypeAsync() : "None";
            var rabbitMQConnected = _rabbitMQService?.IsConnected ?? false;

            var pendingCount = await _dbContext.TranscriptionQueueItems
                .CountAsync(t => t.Status == TranscriptionStatus.Pending);
            
            var processingCount = await _dbContext.TranscriptionQueueItems
                .CountAsync(t => t.Status == TranscriptionStatus.Processing);
            
            var failedCount = await _dbContext.TranscriptionQueueItems
                .CountAsync(t => t.Status == TranscriptionStatus.Failed);
            
            var completedCount = await _dbContext.TranscriptionQueueItems
                .CountAsync(t => t.Status == TranscriptionStatus.Completed);

            var recentFailures = await _dbContext.TranscriptionQueueItems
                .Where(t => t.Status == TranscriptionStatus.Failed)
                .OrderByDescending(t => t.ProcessedAt)
                .Take(10)
                .Select(t => new
                {
                    videoId = t.VideoId,
                    retryCount = t.RetryCount,
                    errorMessage = t.ErrorMessage,
                    queuedAt = t.QueuedAt,
                    processedAt = t.ProcessedAt
                })
                .ToListAsync();

            return Ok(new
            {
                isEnabled,
                provider,
                rabbitMQConnected,
                pendingCount,
                processingCount,
                failedCount,
                completedCount,
                recentFailures
            });
        }

        [HttpPost("transcription/batch")]
        public async Task<IActionResult> BatchTranscribeVideos()
        {
            if (_providerFactory == null || !await _providerFactory.IsEnabledAsync())
            {
                return BadRequest(new { message = "Transcription is not enabled" });
            }

            if (_rabbitMQService == null || !_rabbitMQService.IsConnected)
            {
                return BadRequest(new { message = "RabbitMQ is not connected" });
            }

            // Find all videos without transcripts and not already queued
            var videosWithoutTranscripts = await _dbContext.Videos
                .Where(v => v.Transcript == null)
                .Where(v => !_dbContext.TranscriptionQueueItems.Any(q => q.VideoId == v.Id))
                .ToListAsync();

            var provider = await _providerFactory.GetConfiguredProviderTypeAsync();
            var queuedCount = 0;

            foreach (var video in videosWithoutTranscripts)
            {
                var queueItem = new TranscriptionQueueItem
                {
                    VideoId = video.Id,
                    Status = TranscriptionStatus.Pending,
                    QueuedAt = DateTime.UtcNow,
                    Provider = provider
                };

                _dbContext.TranscriptionQueueItems.Add(queueItem);
                await _dbContext.SaveChangesAsync();

                await _rabbitMQService.PublishTranscriptionMessageAsync(video.Id, provider);
                queuedCount++;
            }

            _logger.LogInformation("Batch transcription queued {Count} videos", queuedCount);

            return Ok(new { message = $"Queued {queuedCount} videos for transcription" });
        }

        [HttpPost("transcription/retry/{videoId}")]
        public async Task<IActionResult> RetryTranscription(int videoId)
        {
            if (_providerFactory == null || !await _providerFactory.IsEnabledAsync())
            {
                return BadRequest(new { message = "Transcription is not enabled" });
            }

            if (_rabbitMQService == null || !_rabbitMQService.IsConnected)
            {
                return BadRequest(new { message = "RabbitMQ is not connected" });
            }

            var queueItem = await _dbContext.TranscriptionQueueItems
                .FirstOrDefaultAsync(q => q.VideoId == videoId);

            if (queueItem == null)
            {
                return NotFound(new { message = "Transcription queue item not found" });
            }

            // Reset status and retry
            queueItem.Status = TranscriptionStatus.Pending;
            queueItem.RetryCount = 0;
            queueItem.ErrorMessage = null;
            queueItem.QueuedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            await _rabbitMQService.PublishTranscriptionMessageAsync(videoId, queueItem.Provider);

            _logger.LogInformation("Retry transcription for video {VideoId}", videoId);

            return Ok(new { message = "Transcription retry queued" });
        }
        
        [HttpGet("transcription/config")]
        public async Task<IActionResult> GetTranscriptionConfig()
        {
            var config = await _dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            if (config == null)
            {
                return Ok(new
                {
                    sttProvider = "None",
                    whisperUrl = "",
                    whisperModel = "",
                    openAiApiKey = "",
                    azureSpeechKey = "",
                    azureSpeechRegion = ""
                });
            }

            return Ok(new
            {
                sttProvider = config.SttProvider ?? "None",
                whisperUrl = config.WhisperUrl ?? "",
                whisperModel = config.WhisperModel ?? "",
                openAiApiKey = string.IsNullOrEmpty(config.OpenAiApiKey) ? "" : "***",
                azureSpeechKey = string.IsNullOrEmpty(config.AzureSpeechKey) ? "" : "***",
                azureSpeechRegion = config.AzureSpeechRegion ?? ""
            });
        }

        [HttpPost("transcription/config")]
        public async Task<IActionResult> UpdateTranscriptionConfig([FromBody] UpdateTranscriptionConfigRequest request)
        {
            var config = await _dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            if (config == null)
            {
                config = new SearchIndexConfiguration();
                _dbContext.SearchIndexConfigurations.Add(config);
            }

            config.SttProvider = request.SttProvider ?? "None";
            config.WhisperUrl = request.WhisperUrl;
            config.WhisperModel = request.WhisperModel;
            
            // Only update API keys if they're provided (not the masked value)
            if (!string.IsNullOrEmpty(request.OpenAiApiKey) && request.OpenAiApiKey != "***")
            {
                config.OpenAiApiKey = request.OpenAiApiKey;
            }
            if (!string.IsNullOrEmpty(request.AzureSpeechKey) && request.AzureSpeechKey != "***")
            {
                config.AzureSpeechKey = request.AzureSpeechKey;
            }
            config.AzureSpeechRegion = request.AzureSpeechRegion;
            
            config.LastModified = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            var safeProviderForLog = (config.SttProvider ?? "None")
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty);
            _logger.LogInformation("Transcription configuration updated: Provider={Provider}", safeProviderForLog);

            return Ok(new { message = "Transcription configuration updated successfully" });
        }
    }

    public class UpdateConfigRequest
    {
        public int SyncIntervalMinutes { get; set; }
    }
    
    public class UpdateTranscriptionConfigRequest
    {
        public string? SttProvider { get; set; }
        public string? WhisperUrl { get; set; }
        public string? WhisperModel { get; set; }
        public string? OpenAiApiKey { get; set; }
        public string? AzureSpeechKey { get; set; }
        public string? AzureSpeechRegion { get; set; }
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
