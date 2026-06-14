using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Outbox worker for the search index. The SearchIndexOperations table is the queue:
    /// rows are written in the same SaveChanges as the video change that caused them, and
    /// this worker polls the table, applies each operation to OpenSearch, and deletes the
    /// row on success. Failed rows are retried in place with capped exponential backoff —
    /// they are never duplicated and never permanently abandoned, so an OpenSearch outage
    /// heals on its own once the cluster is back.
    /// </summary>
    public class SearchIndexBackgroundService : BackgroundService
    {
        private const int BatchSize = 25;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

        private readonly SearchIndexSignal _signal;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SearchIndexBackgroundService> _logger;

        public SearchIndexBackgroundService(
            SearchIndexSignal signal,
            IServiceProvider serviceProvider,
            ILogger<SearchIndexBackgroundService> logger)
        {
            _signal = signal;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Search Index Background Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var processedAny = await ProcessDueOperationsAsync(stoppingToken);
                    if (!processedAny)
                    {
                        await _signal.WaitAsync(PollInterval, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in search index worker loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            _logger.LogInformation("Search Index Background Service stopped");
        }

        private async Task<bool> ProcessDueOperationsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

            // Backoff eligibility is computed in memory because it depends on RetryCount;
            // the table only ever holds a handful of rows, so over-fetching is cheap.
            var now = DateTime.UtcNow;
            // No-tracking: each operation is applied with a set-based ExecuteDelete/ExecuteUpdate
            // keyed by Id, so the change tracker is never used to persist these rows.
            var candidates = await dbContext.SearchIndexOperations
                .AsNoTracking()
                .OrderBy(o => o.Id)
                .Take(200)
                .ToListAsync(cancellationToken);

            var dueOperations = candidates
                .Where(o => IsDue(o, now))
                .Take(BatchSize)
                .ToList();

            foreach (var operation in dueOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessOperationAsync(dbContext, searchService, operation, cancellationToken);
            }

            return dueOperations.Count > 0;
        }

        private static bool IsDue(SearchIndexOperation operation, DateTime now)
        {
            if (operation.LastAttempt == null) return true;
            return operation.LastAttempt.Value + BackoffFor(operation.RetryCount) <= now;
        }

        private static TimeSpan BackoffFor(int retryCount)
        {
            var seconds = 30 * Math.Pow(2, Math.Min(retryCount, 10));
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
        }

        private async Task ProcessOperationAsync(
            TikTokArchiveDbContext dbContext,
            ISearchService searchService,
            SearchIndexOperation operation,
            CancellationToken cancellationToken)
        {
            try
            {
                if (operation.OperationType == SearchIndexOperationType.Index)
                {
                    var video = await dbContext.Videos
                        .AsNoTracking()
                        .Include(v => v.Creator)
                        .Include(v => v.Tags).ThenInclude(vt => vt.Tag)
                        .FirstOrDefaultAsync(v => v.TikTokVideoId == operation.VideoId, cancellationToken);

                    if (video != null)
                    {
                        await searchService.IndexVideoAsync(video, cancellationToken);
                    }
                    else
                    {
                        // The video was deleted before this row was processed; make sure
                        // the index agrees rather than leaving the decision to the sync sweep.
                        await searchService.DeleteVideoAsync(operation.VideoId, cancellationToken);
                    }
                }
                else
                {
                    await searchService.DeleteVideoAsync(operation.VideoId, cancellationToken);
                }

                // Idempotent removal. A set-based delete keyed by Id avoids the change tracker's
                // "expected exactly 1 row" optimistic-concurrency check: under EnableRetryOnFailure
                // a committed delete can be re-executed on a transient retry, and that must affect
                // 0 rows harmlessly rather than throw DbUpdateConcurrencyException and crash the loop.
                await dbContext.SearchIndexOperations
                    .Where(o => o.Id == operation.Id)
                    .ExecuteDeleteAsync(cancellationToken);

                _logger.LogDebug("Processed {OperationType} for video {VideoId}",
                    operation.OperationType, operation.VideoId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing {OperationType} for video {VideoId} (attempt {Attempt})",
                    operation.OperationType, operation.VideoId, operation.RetryCount + 1);

                // Same reasoning as the delete above: a set-based update won't throw if the row
                // is already gone (0 rows affected), so the worker loop survives.
                await dbContext.SearchIndexOperations
                    .Where(o => o.Id == operation.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.RetryCount, operation.RetryCount + 1)
                        .SetProperty(o => o.LastAttempt, DateTime.UtcNow)
                        .SetProperty(o => o.ErrorMessage, ex.GetFullMessage()), cancellationToken);
            }
        }
    }
}
