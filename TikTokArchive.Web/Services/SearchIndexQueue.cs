using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class SearchIndexQueueItem
    {
        public SearchIndexOperationType OperationType { get; set; }
        public string VideoId { get; set; } = string.Empty;
    }

    public class SearchIndexQueue
    {
        private readonly Channel<SearchIndexQueueItem> _channel;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SearchIndexQueue> _logger;

        public SearchIndexQueue(IServiceProvider serviceProvider, ILogger<SearchIndexQueue> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _channel = Channel.CreateUnbounded<SearchIndexQueueItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public async Task EnqueueAsync(SearchIndexOperationType operationType, string videoId)
        {
            try
            {
                // Persist to database first
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var operation = new SearchIndexOperation
                {
                    OperationType = operationType,
                    VideoId = videoId,
                    RetryCount = 0,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.SearchIndexOperations.Add(operation);
                await dbContext.SaveChangesAsync();

                // Then add to in-memory queue
                await _channel.Writer.WriteAsync(new SearchIndexQueueItem
                {
                    OperationType = operationType,
                    VideoId = videoId
                });

                _logger.LogDebug("Enqueued {OperationType} operation for video {VideoId}", operationType, videoId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueuing search index operation for video {VideoId}", videoId);
            }
        }

        public async Task<SearchIndexQueueItem?> DequeueAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _channel.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async Task<int> GetPendingCountAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            return await dbContext.SearchIndexOperations.CountAsync();
        }

        public async Task RequeueFailedOperationsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var failedOperations = await dbContext.SearchIndexOperations
                    .Where(o => o.RetryCount < 3)
                    .OrderBy(o => o.CreatedAt)
                    .Take(100)
                    .ToListAsync();

                foreach (var operation in failedOperations)
                {
                    await _channel.Writer.WriteAsync(new SearchIndexQueueItem
                    {
                        OperationType = operation.OperationType,
                        VideoId = operation.VideoId
                    });
                }

                _logger.LogInformation("Requeued {Count} failed operations", failedOperations.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requeuing failed operations");
            }
        }
    }
}
