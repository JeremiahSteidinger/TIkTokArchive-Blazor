using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class SearchSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SearchSyncBackgroundService> _logger;
        private readonly SearchIndexQueue _queue;

        public SearchSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<SearchSyncBackgroundService> logger,
            SearchIndexQueue queue)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _queue = queue;
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

            try
            {
                var dbVideoIds = await dbContext.Videos
                    .Select(v => v.TikTokVideoId)
                    .ToListAsync(cancellationToken);

                var indexedVideoIds = await searchService.GetIndexedVideoIdsAsync();

                var missingFromIndex = dbVideoIds.Except(indexedVideoIds).ToList();
                var missingFromDb = indexedVideoIds.Except(dbVideoIds).ToList();

                _logger.LogInformation(
                    "Sync found {MissingFromIndex} videos to index and {MissingFromDb} to remove",
                    missingFromIndex.Count, missingFromDb.Count);

                foreach (var videoId in missingFromIndex)
                {
                    await _queue.EnqueueAsync(SearchIndexOperationType.Index, videoId);
                }

                foreach (var videoId in missingFromDb)
                {
                    await _queue.EnqueueAsync(SearchIndexOperationType.Delete, videoId);
                }

                _logger.LogInformation("Search index sync completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during search sync");
            }
        }
    }
}
