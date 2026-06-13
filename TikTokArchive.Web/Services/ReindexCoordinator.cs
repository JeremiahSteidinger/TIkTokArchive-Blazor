using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class ReindexStatus
    {
        public bool IsRunning { get; init; }
        public int ProcessedCount { get; init; }
        public int TotalCount { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Runs at most one bulk reindex at a time and exposes its progress, so the admin UI
    /// and API observe the same job instead of each spawning their own.
    /// </summary>
    public class ReindexCoordinator
    {
        private const int BatchSize = 100;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISearchService _searchService;
        private readonly ILogger<ReindexCoordinator> _logger;
        private readonly object _lock = new();
        private ReindexStatus _status = new();

        public ReindexCoordinator(
            IServiceScopeFactory scopeFactory,
            ISearchService searchService,
            ILogger<ReindexCoordinator> logger)
        {
            _scopeFactory = scopeFactory;
            _searchService = searchService;
            _logger = logger;
        }

        public ReindexStatus Status
        {
            get { lock (_lock) return _status; }
        }

        public bool TryStart()
        {
            lock (_lock)
            {
                if (_status.IsRunning) return false;
                _status = new ReindexStatus { IsRunning = true, StartedAt = DateTime.UtcNow };
            }

            _ = Task.Run(RunAsync);
            return true;
        }

        private async Task RunAsync()
        {
            var startedAt = Status.StartedAt;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var totalVideos = await dbContext.Videos.CountAsync();
                UpdateStatus(new ReindexStatus
                {
                    IsRunning = true,
                    TotalCount = totalVideos,
                    StartedAt = startedAt
                });

                _logger.LogInformation("Starting bulk reindex of {TotalVideos} videos", totalVideos);

                var processedCount = 0;
                for (var skip = 0; skip < totalVideos; skip += BatchSize)
                {
                    var videos = await dbContext.Videos
                        .Include(v => v.Creator)
                        .Include(v => v.Tags).ThenInclude(vt => vt.Tag)
                        .OrderBy(v => v.Id)
                        .Skip(skip)
                        .Take(BatchSize)
                        .ToListAsync();

                    await _searchService.IndexVideosAsync(videos);

                    processedCount += videos.Count;
                    UpdateStatus(new ReindexStatus
                    {
                        IsRunning = true,
                        ProcessedCount = processedCount,
                        TotalCount = totalVideos,
                        StartedAt = startedAt
                    });
                }

                UpdateStatus(new ReindexStatus
                {
                    ProcessedCount = processedCount,
                    TotalCount = totalVideos,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow
                });

                _logger.LogInformation("Bulk reindex completed: {ProcessedCount} videos indexed", processedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bulk reindex");
                var previous = Status;
                UpdateStatus(new ReindexStatus
                {
                    ProcessedCount = previous.ProcessedCount,
                    TotalCount = previous.TotalCount,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow,
                    ErrorMessage = ex.Message
                });
            }
        }

        private void UpdateStatus(ReindexStatus status)
        {
            lock (_lock) _status = status;
        }
    }
}
