using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class SearchIndexBackgroundService : BackgroundService
    {
        private readonly SearchIndexQueue _queue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SearchIndexBackgroundService> _logger;

        public SearchIndexBackgroundService(
            SearchIndexQueue queue,
            IServiceProvider serviceProvider,
            ILogger<SearchIndexBackgroundService> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Search Index Background Service started");

            // Requeue any failed operations on startup
            await _queue.RequeueFailedOperationsAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var item = await _queue.DequeueAsync(stoppingToken);
                    if (item == null) continue;

                    await ProcessItemAsync(item, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing search index queue item");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            _logger.LogInformation("Search Index Background Service stopped");
        }

        private async Task ProcessItemAsync(SearchIndexQueueItem item, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

            // Find the operation in the database
            var operation = await dbContext.SearchIndexOperations
                .FirstOrDefaultAsync(o => o.VideoId == item.VideoId && o.OperationType == item.OperationType, cancellationToken);

            if (operation == null)
            {
                _logger.LogWarning("Operation not found in database for video {VideoId}", item.VideoId);
                return;
            }

            try
            {
                if (item.OperationType == SearchIndexOperationType.Index)
                {
                    var video = await dbContext.Videos
                        .Include(v => v.Creator)
                        .Include(v => v.Tags).ThenInclude(vt => vt.Tag)
                        .FirstOrDefaultAsync(v => v.TikTokVideoId == item.VideoId, cancellationToken);

                    if (video != null)
                    {
                        await searchService.IndexVideoAsync(video);
                        _logger.LogInformation("Successfully indexed video {VideoId}", item.VideoId);
                    }
                    else
                    {
                        _logger.LogWarning("Video {VideoId} not found in database", item.VideoId);
                    }
                }
                else if (item.OperationType == SearchIndexOperationType.Delete)
                {
                    await searchService.DeleteVideoAsync(item.VideoId);
                    _logger.LogInformation("Successfully deleted video {VideoId} from index", item.VideoId);
                }

                // Remove from database on success
                dbContext.SearchIndexOperations.Remove(operation);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing {OperationType} for video {VideoId}", 
                    item.OperationType, item.VideoId);

                // Update retry count
                operation.RetryCount++;
                operation.LastAttempt = DateTime.UtcNow;
                operation.ErrorMessage = ex.Message;
                await dbContext.SaveChangesAsync(cancellationToken);

                // Requeue if under max retries
                if (operation.RetryCount < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, operation.RetryCount)), cancellationToken);
                    await _queue.EnqueueAsync(item.OperationType, item.VideoId);
                }
                else
                {
                    _logger.LogError("Max retries exceeded for video {VideoId}", item.VideoId);
                }
            }
        }
    }
}
