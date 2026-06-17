using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Periodic reconciliation between the database and the search index. With the
    /// transactional outbox handling normal writes, this is a safety net for drift
    /// (e.g., a wiped OpenSearch volume or the initial population of a new index).
    /// It enqueues outbox rows rather than touching the index directly, and skips
    /// videos that already have a pending row so retrying operations are not duplicated.
    /// </summary>
    public class SearchSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SearchSyncBackgroundService> _logger;
        private readonly SearchIndexSignal _signal;
        private readonly BackgroundTaskMonitor _monitor;

        public SearchSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<SearchSyncBackgroundService> logger,
            SearchIndexSignal signal,
            BackgroundTaskMonitor monitor)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _signal = signal;
            _monitor = monitor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Search Sync Background Service started");

            // Wait for initial startup
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var interval = await GetSyncIntervalAsync();
                    await PerformSyncAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in sync background service");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Search Sync Background Service stopped");
        }

        private async Task<int> GetSyncIntervalAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

            var config = await dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            if (config == null)
            {
                config = new SearchIndexConfiguration { SyncIntervalMinutes = 30 };
                dbContext.SearchIndexConfigurations.Add(config);
                await dbContext.SaveChangesAsync();
            }

            return config.SyncIntervalMinutes;
        }

        private async Task PerformSyncAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

            _logger.LogInformation("Starting search index sync");
            _monitor.BeginItem("search-sync", "Reconciling database ↔ index", null, "Comparing");
            string? error = null;

            try
            {
                var dbVideoIds = await dbContext.Videos
                    .Select(v => v.TikTokVideoId)
                    .ToListAsync(cancellationToken);

                // Throws if the index can't be enumerated, so a transient failure skips
                // the cycle instead of being mistaken for an empty index.
                var indexedVideoIds = await searchService.GetIndexedVideoIdsAsync(cancellationToken);

                var pendingVideoIds = (await dbContext.SearchIndexOperations
                    .Select(o => o.VideoId)
                    .ToListAsync(cancellationToken)).ToHashSet();

                var missingFromIndex = dbVideoIds.Except(indexedVideoIds)
                    .Where(id => !pendingVideoIds.Contains(id))
                    .ToList();
                var missingFromDb = indexedVideoIds.Except(dbVideoIds)
                    .Where(id => !pendingVideoIds.Contains(id))
                    .ToList();

                _logger.LogInformation(
                    "Sync found {MissingFromIndex} videos to index and {MissingFromDb} to remove",
                    missingFromIndex.Count, missingFromDb.Count);

                if (missingFromIndex.Count == 0 && missingFromDb.Count == 0) return;

                foreach (var videoId in missingFromIndex)
                {
                    dbContext.SearchIndexOperations.Add(new SearchIndexOperation
                    {
                        OperationType = SearchIndexOperationType.Index,
                        VideoId = videoId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                foreach (var videoId in missingFromDb)
                {
                    dbContext.SearchIndexOperations.Add(new SearchIndexOperation
                    {
                        OperationType = SearchIndexOperationType.Delete,
                        VideoId = videoId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                _signal.Notify();

                _logger.LogInformation("Search index sync completed");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogError(ex, "Error during search sync");
            }
            finally
            {
                _monitor.CompleteItem("search-sync", error);
            }
        }
    }
}
